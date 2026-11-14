import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
import { payoutOf } from './economy';
import { EconomySection } from './Inventory';
import { betsFixture, economyFixture, petyaId } from './inventoryFixtures';

// Bets on the season screen (4.11c, D-414, D-447): only the runs the server says a bet is accepted on, the sheet with
// the deadline and its multiplier, the stake within my limit and the win said before the press; one command; a refusal
// in Russian in the sheet; my bets with their status in words; empty, refused, error; hidden when bets are off.

const economyUrl = `GET /api/seasons/${seasonId}/economy`;
const betUrl = `POST /api/seasons/${seasonId}/bets`;

function renderBets(bets = true) {
  return render(
    <EconomySection seasonId={seasonId} version={1} onSignedOut={vi.fn()} bets={bets} />,
  );
}

describe('the bets', () => {
  it('lists the runs open for a bet and my bets with their status', async () => {
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture() }) });

    renderBets();

    const run = await screen.findByTestId(`bet-run-${petyaId}`);
    expect(within(run).getByText('Петя')).toBeInTheDocument();
    expect(within(run).getByText('Silent Hill 2')).toBeInTheDocument();
    expect(within(run).getByText(ru.bets.hours(6))).toBeInTheDocument();
    expect(screen.getByTestId('bets-open')).toHaveTextContent(ru.bets.open(1, 3));
    const mine = screen.getByTestId('my-bets');
    expect(within(mine).getAllByRole('listitem')).toHaveLength(3);
    expect(screen.getByTestId('bet-status-b1000000-0000-0000-0000-000000000002')).toHaveTextContent(
      `${ru.bets.status.won} ${ru.bets.wonPayout(9)}`,
    );
    expect(screen.getByTestId('bet-status-b1000000-0000-0000-0000-000000000003')).toHaveTextContent(
      ru.bets.status.lost,
    );
  });

  it('places a bet with the deadline and the stake picked, saying the win before the press', async () => {
    const server = fakeServer({
      [economyUrl]: economyFixture({ bets: betsFixture() }),
      [betUrl]: { duplicate: false, events: [] },
    });
    renderBets();

    await userEvent.click(await screen.findByRole('button', { name: ru.bets.placeOn('Петя') }));
    const sheet = await screen.findByTestId('bet-sheet');
    expect(within(sheet).getByText(ru.bets.dialogTitle('Петя'))).toBeInTheDocument();
    await userEvent.click(within(sheet).getByText(ru.bets.option(3, 2)));
    const stake = within(sheet).getByLabelText(ru.bets.stake);
    await userEvent.clear(stake);
    await userEvent.type(stake, '5');
    expect(within(sheet).getByTestId('bet-payout')).toHaveTextContent(
      ru.bets.payout(payoutOf(5, 2), 5),
    );
    await userEvent.click(within(sheet).getByTestId('bet-confirm'));

    await waitFor(() => {
      expect(screen.queryByTestId('bet-sheet')).not.toBeInTheDocument();
    });
    expect(server.sent('POST', '/bets')[0]?.body).toMatchObject({
      onPlayerId: petyaId,
      days: 3,
      stake: 5,
    });
    expect(screen.getByRole('status')).toHaveTextContent(ru.bets.placed('Петя'));
  });

  it('keeps the press closed for a stake beyond my limit and says the limit', async () => {
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture() }) });
    renderBets();
    await userEvent.click(await screen.findByTestId(`bet-on-${petyaId}`));
    const sheet = await screen.findByTestId('bet-sheet');

    const stake = within(sheet).getByLabelText(ru.bets.stake);
    await userEvent.clear(stake);
    await userEvent.type(stake, '8');

    expect(within(sheet).getByTestId('bet-confirm')).toBeDisabled();
    expect(within(sheet).getByText(ru.bets.stakeInvalid(7))).toBeInTheDocument();
  });

  it('says a refusal of the server in the sheet', async () => {
    fakeServer({
      [economyUrl]: economyFixture({ bets: betsFixture() }),
      [betUrl]: answer(409, { title: 'x', code: 'bet.windowClosed' }),
    });
    renderBets();
    await userEvent.click(await screen.findByTestId(`bet-on-${petyaId}`));
    const sheet = await screen.findByTestId('bet-sheet');

    await userEvent.click(within(sheet).getByTestId('bet-confirm'));

    expect(await within(sheet).findByRole('alert')).toHaveTextContent(
      ru.rejection['bet.windowClosed'],
    );
    expect(screen.getByTestId('bet-sheet')).toBeInTheDocument();
  });

  it('is empty when nobody is open for a bet, and says why when no bet is placed at all', async () => {
    fakeServer({
      [economyUrl]: economyFixture({ bets: betsFixture({ runs: [], bets: [] }) }),
    });
    const { unmount } = renderBets();
    // S4D-4: empty is said once — one empty state with the lead folded in, no empty «Мои ставки» under it
    expect(await screen.findByTestId('bets-empty')).toHaveTextContent(ru.bets.emptyTitle);
    expect(screen.getByTestId('bets-empty')).toHaveTextContent(ru.bets.emptyText);
    expect(screen.queryByText(ru.bets.lead)).not.toBeInTheDocument();
    expect(screen.queryByText(ru.bets.mineTitle)).not.toBeInTheDocument();
    expect(screen.queryByText(ru.bets.runsTitle)).not.toBeInTheDocument();
    unmount();

    fakeServer({
      [economyUrl]: economyFixture({
        bets: betsFixture({ refusal: 'bet.tooMany', runs: [], openBets: 3 }),
      }),
    });
    renderBets();
    expect(await screen.findByTestId('bets-refusal')).toHaveTextContent(
      ru.rejection['bet.tooMany'],
    );
  });

  it('shows the error state and is hidden when bets are off', async () => {
    fakeServer({ [economyUrl]: answer(500, { title: 'x' }) });
    const { unmount } = renderBets();
    expect(await screen.findByText(ru.bets.loadErrorTitle)).toBeInTheDocument();
    unmount();

    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture() }) });
    renderBets(false);
    await screen.findByTestId('inventory-count');
    expect(screen.queryByTestId('bets')).not.toBeInTheDocument();
  });

  it('says the shorter deadline pays more only when the multipliers differ', async () => {
    const flat = betsFixture().runs.map((r) => ({
      ...r,
      hours: null,
      options: r.options.map((o) => ({ ...o, multiplier: 1.2 })),
    }));
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture({ runs: flat }) }) });
    renderBets();
    await userEvent.click(await screen.findByTestId(`bet-on-${petyaId}`));
    const sheet = await screen.findByTestId('bet-sheet');

    expect(within(sheet).queryByText(ru.bets.deadlineHint)).not.toBeInTheDocument();
    expect(screen.getByText(ru.bets.noHours)).toBeInTheDocument();
  });

  it('says the stake range once in the sheet: the error replaces the hint, no reason above the press (S4D-7)', async () => {
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture() }) });
    renderBets();
    await userEvent.click(await screen.findByTestId(`bet-on-${petyaId}`));
    const sheet = await screen.findByTestId('bet-sheet');

    const stake = within(sheet).getByLabelText(ru.bets.stake);
    await userEvent.clear(stake);
    await userEvent.type(stake, '8');

    expect(within(sheet).getByText(ru.bets.stakeInvalid(7))).toBeInTheDocument();
    expect(within(sheet).queryByText(ru.bets.stakeHint(7))).not.toBeInTheDocument();
    expect(within(sheet).queryByTestId('bet-payout')).not.toBeInTheDocument();
  });

  it('says what comes back for a stake, and no win on top when the multiplier adds nothing (S4D-7)', async () => {
    const flat = betsFixture().runs.map((r) => ({
      ...r,
      options: r.options.map((o) => ({ ...o, multiplier: 1.2 })),
    }));
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture({ runs: flat }) }) });
    renderBets();
    await userEvent.click(await screen.findByTestId(`bet-on-${petyaId}`));
    const sheet = await screen.findByTestId('bet-sheet');

    expect(within(sheet).getByTestId('bet-payout')).toHaveTextContent(
      'Вернётся 1 монетка: ставка 1, без выигрыша сверху',
    );
    const stake = within(sheet).getByLabelText(ru.bets.stake);
    await userEvent.clear(stake);
    await userEvent.type(stake, '5');
    expect(within(sheet).getByTestId('bet-payout')).toHaveTextContent(
      'Вернётся 6 монеток: ставка 5 и +1 сверху',
    );
  });

  it('says a refusal shared by the whole panel as its one empty state (S4D-4)', async () => {
    fakeServer({
      [economyUrl]: economyFixture({
        bets: betsFixture({ refusal: 'roll.notEnoughCoins', runs: [], bets: [] }),
      }),
    });
    renderBets();
    const empty = await screen.findByTestId('bets-refusal');
    expect(empty).toHaveTextContent('Нет монеток на ставку');
    expect(empty).toHaveTextContent(ru.bets.lead);
    expect(screen.queryByText(ru.bets.mineTitle)).not.toBeInTheDocument();
  });
});

describe('the bet sheet', () => {
  it('marks the chosen deadline with a check besides the fill and keeps the press at the bottom (S4D-2, S4D-17)', async () => {
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture() }) });
    renderBets();
    await userEvent.click(await screen.findByTestId(`bet-on-${petyaId}`));
    const sheet = await screen.findByTestId('bet-sheet');

    await userEvent.click(within(sheet).getByText(ru.bets.option(3, 2)));
    const chosen = within(sheet).getByText(ru.bets.option(3, 2)).closest('label');
    const other = within(sheet).getByText(ru.bets.option(1, 3)).closest('label');
    expect(chosen?.querySelector('.lucide-check')).not.toBeNull();
    expect(other?.querySelector('.lucide-check')).toBeNull();
    expect(within(sheet).getByTestId('form-dialog-footer')).toContainElement(
      within(sheet).getByTestId('bet-confirm'),
    );
  });
});

// S4D-3: with 16 players the panel was ~6000 px on a phone
describe('the bets with many players', () => {
  const [petya] = betsFixture().runs;
  if (!petya) throw new Error('No run in the fixture.');
  const many = (n: number, closesAt = '2026-11-12T09:30:00Z') =>
    Array.from({ length: n }, (_, i) => ({
      ...petya,
      playerId: `p${String(i).padStart(7, '0')}-0000-0000-0000-000000000000`,
      runId: `r${String(i).padStart(7, '0')}-0000-0000-0000-000000000000`,
      name: `Игрок ${String(i + 1)}`,
      hours: i % 2 === 0 ? 6 : null,
      closesAt,
    }));

  it('puts my bets first, then the runs', async () => {
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture({ runs: many(3) }) }) });
    renderBets();
    const runs = await screen.findByTestId('bet-runs');
    const mine = screen.getByTestId('my-bets');
    expect(mine.compareDocumentPosition(runs) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('says a shared deadline once and the hours only when they are known', async () => {
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture({ runs: many(4) }) }) });
    renderBets();
    await screen.findByTestId('bet-runs');
    expect(screen.getAllByText(/^Ставки до /)).toHaveLength(1);
    expect(screen.queryByText(ru.bets.noHours)).not.toBeInTheDocument();
    expect(screen.getAllByText(ru.bets.hours(6))).toHaveLength(2);
  });

  it('keeps a deadline on each row when the deadlines differ', async () => {
    const runs = [
      ...many(1),
      ...many(1, '2026-11-12T11:00:00Z').map((r) => ({ ...r, playerId: 'x', runId: 'y' })),
    ];
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture({ runs }) }) });
    renderBets();
    await screen.findByTestId('bet-runs');
    expect(screen.getAllByText(/^Ставки до /)).toHaveLength(2);
  });

  it('shows the first five rows and folds the rest behind «Ещё N игроков»', async () => {
    fakeServer({ [economyUrl]: economyFixture({ bets: betsFixture({ runs: many(16) }) }) });
    renderBets();
    const runs = await screen.findByTestId('bet-runs');
    expect(within(runs).getAllByRole('button', { name: /^Поставить на игрока/ })).toHaveLength(5);
    const more = screen.getByTestId('bet-runs-more');
    expect(within(more).getByText('Ещё 11 игроков')).toBeInTheDocument();
    expect(more.querySelectorAll('li')).toHaveLength(11);
    // Every row keeps its own «Поставить»
    expect(screen.getByTestId(`bet-on-${many(16)[15]?.playerId ?? ''}`)).toBeInTheDocument();
  });

  it('says «Ещё N игроков» in the right plural', () => {
    expect(ru.bets.more(1)).toBe('Ещё 1 игрок');
    expect(ru.bets.more(3)).toBe('Ещё 3 игрока');
    expect(ru.bets.more(11)).toBe('Ещё 11 игроков');
    expect(ru.bets.more(21)).toBe('Ещё 21 игрок');
  });
});
