import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
import { EconomySection } from './Inventory';
import { ShopPanel } from './Shop';
import { timeLeft } from './economy';
import { economyFixture, fixtureNow, shopFixture } from './inventoryFixtures';

// My shop on the season screen (4.11c, D-443, D-446): the roll with its price or a coupon, the lots with their timer,
// «Купить» only on a lot the engine sells now (otherwise its reason in words), one command per press, a refusal said in
// Russian; the lots vanish when their time is up; loading, empty and error states; hidden when the shop is off.

const economyUrl = `GET /api/seasons/${seasonId}/economy`;
const rollUrl = `POST /api/seasons/${seasonId}/shop/roll`;
const buyUrl = `POST /api/seasons/${seasonId}/shop/buy`;
const ok = { duplicate: false, events: [] };

function renderShop(shop = true) {
  return render(
    <EconomySection seasonId={seasonId} version={1} onSignedOut={vi.fn()} shop={shop} />,
  );
}

function liveShop(over: Parameters<typeof shopFixture>[0] = {}) {
  // The fixture's lots last 12:34 from its clock; the tests run on the real clock
  return shopFixture({ expiresAt: new Date(Date.now() + 754_000).toISOString(), ...over });
}

describe('the shop', () => {
  it('lists the lots with their price, a buy button only where the engine sells', async () => {
    fakeServer({ [economyUrl]: economyFixture({ shop: liveShop() }) });

    renderShop();

    const lots = await screen.findByTestId('shop-lots');
    expect(within(lots).getAllByRole('listitem')).toHaveLength(3);
    expect(screen.getByTestId('lot-price-0')).toHaveTextContent('4 монетки');
    expect(
      screen.getByRole('button', { name: ru.shop.buyNamed('Апельсин', 4) }),
    ).toBeInTheDocument();
    expect(screen.getByTestId('lot-why-1')).toHaveTextContent(
      ru.shop.refusals['roll.notEnoughCoins'] ?? '',
    );
    expect(within(screen.getByTestId('lot-2')).getByText(ru.shop.sold)).toBeInTheDocument();
    expect(screen.queryByTestId('buy-1')).not.toBeInTheDocument();
    expect(screen.queryByTestId('buy-2')).not.toBeInTheDocument();
    expect(screen.getByTestId('shop-timer')).toHaveTextContent(/Лоты исчезнут через 12:3\d/);
    expect(screen.getByTestId('shop-roll')).toHaveTextContent(ru.shop.rollAgain(6));
  });

  it('buys a lot with one command and says it is in the inventory', async () => {
    const server = fakeServer({ [economyUrl]: economyFixture({ shop: liveShop() }), [buyUrl]: ok });
    renderShop();

    await userEvent.click(await screen.findByTestId('buy-0'));

    await waitFor(() => {
      expect(server.sent('POST', '/shop/buy')).toHaveLength(1);
    });
    const body = server.sent('POST', '/shop/buy')[0]?.body as { lot: number; commandId: string };
    expect(body.lot).toBe(0);
    expect(typeof body.commandId).toBe('string');
    expect(await screen.findByTestId('shop-announce')).toHaveTextContent(
      ru.shop.bought('Апельсин'),
    );
    // The economy loads again after the purchase
    await waitFor(() => {
      expect(server.sent('GET', '/economy').length).toBeGreaterThanOrEqual(2);
    });
  });

  it('says a refusal of the purchase in Russian', async () => {
    fakeServer({
      [economyUrl]: economyFixture({ shop: liveShop() }),
      [buyUrl]: answer(409, { title: 'x', code: 'inventory.full' }),
    });
    renderShop();

    await userEvent.click(await screen.findByTestId('buy-0'));

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.rejection['inventory.full']);
  });

  it('rolls with the price shown, or by a coupon', async () => {
    const server = fakeServer({
      [economyUrl]: economyFixture({
        shop: shopFixture({
          payment: 'freeRoll',
          price: 0,
          freeRolls: 2,
          expiresAt: null,
          lots: [],
        }),
      }),
      [rollUrl]: ok,
    });
    renderShop();

    expect(await screen.findByText(ru.shop.emptyTitle)).toBeInTheDocument();
    expect(screen.getByTestId('shop-coupons')).toHaveTextContent(ru.shop.coupons(2));
    const roll = screen.getByTestId('shop-roll');
    expect(roll).toHaveTextContent(ru.shop.rollFree);
    await userEvent.click(roll);

    await waitFor(() => {
      expect(server.sent('POST', '/shop/roll')).toHaveLength(1);
    });
    expect(await screen.findByTestId('shop-announce')).toHaveTextContent(ru.shop.rollAnnounce);
  });

  it('closes the roll and says why when the engine refuses it', async () => {
    fakeServer({
      [economyUrl]: economyFixture({
        coins: -2,
        shop: shopFixture({ refusal: 'coins.inDebt', expiresAt: null, lots: [] }),
      }),
    });
    renderShop();

    expect(await screen.findByTestId('shop-roll')).toBeDisabled();
    expect(screen.getByTestId('shop-refusal')).toHaveTextContent(ru.rejection['coins.inDebt']);
    expect(screen.getByTestId('shop-roll')).toHaveTextContent(ru.shop.roll(6));
  });

  it('hides the lots when their time is up', () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });
    try {
      vi.setSystemTime(fixtureNow);
      render(
        <ShopPanel
          state={{ kind: 'ready', economy: economyFixture({ shop: shopFixture() }) }}
          onRetry={vi.fn()}
          onRoll={vi.fn()}
          onBuy={vi.fn()}
        />,
      );
      expect(screen.getByTestId('shop-timer')).toHaveTextContent('12:34');

      act(() => {
        vi.advanceTimersByTime(754_000);
      });

      expect(screen.queryByTestId('shop-lots')).not.toBeInTheDocument();
      expect(screen.getByText(ru.shop.goneTitle)).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows the error with a retry', async () => {
    fakeServer({ [economyUrl]: answer(500, { title: 'x' }) });
    renderShop();

    expect(await screen.findByText(ru.shop.loadErrorTitle)).toBeInTheDocument();
  });

  it('is not shown when the shop is off', async () => {
    fakeServer({ [economyUrl]: economyFixture({ shop: liveShop() }) });
    renderShop(false);

    await screen.findByTestId('inventory-count');
    expect(screen.queryByTestId('shop')).not.toBeInTheDocument();
  });

  it('is not shown when the server gives no shop', async () => {
    fakeServer({ [economyUrl]: economyFixture({ shop: null }) });
    renderShop();

    await screen.findByTestId('inventory-count');
    expect(screen.queryByTestId('shop')).not.toBeInTheDocument();
  });
});

describe('the timer', () => {
  it('counts minutes and seconds, and hours when there are any', () => {
    const now = new Date('2026-01-01T00:00:00Z');
    expect(timeLeft(new Date(now.getTime() + 754_000), now)).toBe('12:34');
    expect(timeLeft(new Date(now.getTime() + 3_725_000), now)).toBe('1:02:05');
    expect(timeLeft(new Date(now.getTime() - 5_000), now)).toBe('0:00');
  });
});
