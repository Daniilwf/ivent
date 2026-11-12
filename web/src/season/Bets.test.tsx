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
      ru.bets.payout(payoutOf(5, 2)),
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
    expect(await screen.findByText(ru.bets.emptyTitle)).toBeInTheDocument();
    expect(screen.getByText(ru.bets.mineEmpty)).toBeInTheDocument();
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
});
