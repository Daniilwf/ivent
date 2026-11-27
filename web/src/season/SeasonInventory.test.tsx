import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import type { Schemas } from '../api/client';
import { answer, fakeServer, json, seasonId } from '../test/fakeServer';
import { EconomySection } from './Inventory';
import { SeasonScreen } from './SeasonScreen';
import { betsFixture, economyFixture, fixtureNow, shopFixture } from './inventoryFixtures';

// The inventory on the season screen (D-441): only when the season plays items and I am one of its players; a
// mechanic off is not shown at all, and nothing asks the server for it.

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => {},
}));

const me = '10000000-0000-0000-0000-000000000001';

function season(
  items: boolean,
  mine = true,
  more: { shop?: boolean; bets?: boolean } = {},
): Schemas['SeasonView'] {
  return {
    id: seasonId,
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'finish', type: 'finish' },
    ],
    players: [
      {
        id: me,
        name: 'Вася',
        cellId: 'start',
        points: 0,
        phase: 'idle',
        finishOrder: null,
        avatar: null,
        token: 0,
      },
    ],
    leaderboard: [
      { playerId: me, place: 1, points: 0, cellsToFinish: 1, isFirst: false, provisional: false },
    ],
    me: mine
      ? {
          playerId: me,
          phase: 'idle',
          offer: null,
          choice: null,
          activeRun: null,
          lastCompleted: null,
          nextReroll: null,
          manualEffects: [],
          dropHintMinutes: null,
          dropPenalty: null,
          techRerollOpen: false,
          challengesEnabled: false,
          roll: null,
          unchecked: null,
          finish: null,
        }
      : null,
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    name: 'Тестовый сезон',
    economy: { items, shop: more.shop ?? false, bets: more.bets ?? false },
  };
}

function open(view: Schemas['SeasonView']) {
  const server = fakeServer({
    [`GET /api/seasons/${seasonId}`]: view,
    [`GET /api/seasons/${seasonId}/economy`]: economyFixture({
      shop: shopFixture(),
      bets: betsFixture(),
    }),
  });
  render(<SeasonScreen seasonId={seasonId} onSignedOut={() => {}} />);
  return server;
}

describe('the inventory on the season screen', () => {
  it('is there when the season plays items', async () => {
    open(season(true));

    expect(await screen.findByTestId('item-bird-thief')).toBeInTheDocument();
  });

  it('is not shown, and not asked for, with items off', async () => {
    const server = open(season(false));

    expect(await screen.findByTestId('turn')).toBeInTheDocument();
    expect(screen.queryByTestId('inventory')).not.toBeInTheDocument();
    expect(server.sent('GET', '/economy')).toHaveLength(0);
  });

  it('is not shown to a spectator', async () => {
    const server = open(season(true, false));

    expect(await screen.findByTestId('turn')).toBeInTheDocument();
    expect(screen.queryByTestId('inventory')).not.toBeInTheDocument();
    expect(server.sent('GET', '/economy')).toHaveLength(0);
  });

  it('shows the shop and the bets only when the season plays them (D-447)', async () => {
    open(season(true, true, { shop: true, bets: true }));
    expect(await screen.findByTestId('shop')).toBeInTheDocument();
    expect(screen.getByTestId('bets')).toBeInTheDocument();
  });

  it('keeps the shop and the bets hidden when they are off', async () => {
    open(season(true));
    expect(await screen.findByTestId('item-bird-thief')).toBeInTheDocument();
    expect(screen.queryByTestId('shop')).not.toBeInTheDocument();
    expect(screen.queryByTestId('bets')).not.toBeInTheDocument();
  });

  it('shows the bets without the inventory when bets play without items', async () => {
    const server = open(season(false, true, { bets: true }));
    expect(await screen.findByTestId('bets')).toBeInTheDocument();
    expect(screen.queryByTestId('inventory')).not.toBeInTheDocument();
    expect(screen.queryByTestId('shop')).not.toBeInTheDocument();
    expect(server.sent('GET', '/economy')).toHaveLength(1);
  });
});

describe('the season screen order', () => {
  it('puts nothing between the turn card and the leaderboard (S4D-1)', async () => {
    open(season(true, true, { shop: true, bets: true }));
    const shop = await screen.findByTestId('shop');
    const turn = screen.getByTestId('turn');
    const leaders = screen.getByRole('heading', { name: ru.leaderboard.title, hidden: true });
    expect(turn.compareDocumentPosition(leaders) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(leaders.compareDocumentPosition(shop) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});

describe('the compact economy on the season screen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function renderCompact(
    economy: Schemas['EconomyView'],
    { items = true, shop = true, bets = true } = {},
  ) {
    fakeServer({
      [`GET /api/seasons/${seasonId}/economy`]: economy,
      [`POST /api/seasons/${seasonId}/shop/roll`]: answer(409, {
        title: 'rejected',
        code: 'roll.notEnoughCoins',
      }),
    });
    render(
      <EconomySection
        compact
        seasonId={seasonId}
        version={1}
        onSignedOut={vi.fn()}
        items={items}
        shop={shop}
        bets={bets}
      />,
    );
  }

  it('shows only enabled summaries during loading and exposes the retry when a panel is open', async () => {
    let finish!: (response: Response) => void;
    const response = new Promise<Response>((resolve) => {
      finish = resolve;
    });
    fakeServer({ [`GET /api/seasons/${seasonId}/economy`]: () => response });
    render(
      <EconomySection
        compact
        seasonId={seasonId}
        version={1}
        onSignedOut={vi.fn()}
        items={false}
        shop
      />,
    );

    const shop = screen.getByRole('button', { name: 'Магазин' });
    expect(screen.queryByRole('button', { name: /Инвентарь|Ставки/u })).not.toBeInTheDocument();
    await userEvent.click(shop);
    expect(screen.getByTestId('shop').querySelector('[aria-busy=true]')).toBeVisible();
    finish(json(503));
    expect(await screen.findByText(ru.shop.loadErrorTitle)).toBeVisible();
    expect(screen.getByRole('button', { name: ru.ui.retry })).toBeVisible();
  });

  it('starts with independent summaries and keeps expanded panels mounted on a phone', async () => {
    const user = userEvent.setup();
    renderCompact(
      economyFixture({
        shop: shopFixture({ expiresAt: null, lots: [] }),
        bets: betsFixture(),
      }),
    );

    const inventory = await screen.findByRole('button', { name: /Инвентарь.*Предметов 2 из 5/u });
    const shop = screen.getByRole('button', { name: /Магазин.*Ролл за 6 монеток/u });
    const bets = screen.getByRole('button', { name: /Ставки.*Открыто 1 из 3/u });
    expect(inventory).toHaveAttribute('aria-expanded', 'false');
    expect(shop).toHaveAttribute('aria-expanded', 'false');
    expect(bets).toHaveAttribute('aria-expanded', 'false');
    const inventoryPanel = screen.getByTestId('inventory');
    expect(inventoryPanel).not.toBeVisible();

    await user.click(inventory);
    await user.click(bets);
    expect(inventoryPanel).toBeVisible();
    expect(screen.getByTestId('bets')).toBeVisible();
    expect(screen.getByTestId('shop')).not.toBeVisible();

    await user.click(inventory);
    expect(inventoryPanel).not.toBeVisible();
    expect(screen.getByTestId('inventory')).toBe(inventoryPanel);
    expect(screen.getByTestId('bets')).toBeVisible();
  });

  it('updates the collapsed shop after expiration using server time and shows a coupon', async () => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
    try {
      // The device thinks it is an hour later. Only the server's clock may expire a lot.
      vi.setSystemTime(new Date(fixtureNow.getTime() + 3_600_000));
      renderCompact(
        economyFixture({
          items: [],
          serverNow: fixtureNow.toISOString(),
          shop: shopFixture({
            expiresAt: new Date(fixtureNow.getTime() + 1500).toISOString(),
            payment: 'freeRoll',
            freeRolls: 1,
          }),
        }),
        { bets: false },
      );

      expect(
        await screen.findByRole('button', { name: /Инвентарь.*Пусто, 0 из 5/u }),
      ).toBeVisible();
      const summary = screen.getByRole('button', { name: /Магазин.*2 лота в продаже/u });
      await act(() => vi.advanceTimersByTimeAsync(2000));
      expect(summary).toHaveAccessibleName(/Магазин.*Ролл по купону/u);
      expect(summary).toHaveAttribute('aria-expanded', 'false');
    } finally {
      vi.useRealTimers();
    }
  });

  it('segments only enabled mechanics on desktop and keeps the shop state when switching', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: query === '(min-width: 900px)',
      addEventListener() {},
      removeEventListener() {},
    }));
    const user = userEvent.setup();
    renderCompact(
      economyFixture({
        shop: shopFixture({ expiresAt: null, lots: [] }),
        bets: betsFixture(),
      }),
      { items: false },
    );

    const segments = within(screen.getByRole('group', { name: 'Разделы экономики' }));
    expect(segments.queryByRole('button', { name: 'Инвентарь' })).not.toBeInTheDocument();
    const shop = segments.getByRole('button', { name: 'Магазин' });
    const bets = segments.getByRole('button', { name: 'Ставки' });
    expect(shop).toHaveAttribute('aria-pressed', 'true');
    expect(bets).toHaveAttribute('aria-pressed', 'false');
    const shopPanel = await screen.findByTestId('shop');
    const roll = await within(shopPanel).findByTestId('shop-roll');
    await user.click(roll);
    expect(await within(shopPanel).findByRole('alert')).toHaveTextContent(
      ru.rejection['roll.notEnoughCoins'],
    );

    await user.click(bets);
    expect(bets).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByTestId('bets')).toBeVisible();
    expect(shopPanel).not.toBeVisible();
    await user.click(shop);

    expect(screen.getByTestId('shop')).toBe(shopPanel);
    expect(within(shopPanel).getByRole('alert')).toBeVisible();
    expect(screen.getByTestId('bets')).not.toBeVisible();
  });
});
