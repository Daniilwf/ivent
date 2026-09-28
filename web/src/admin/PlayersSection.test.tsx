import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, seasonId } from '../test/fakeServer';
import { PlayersSection } from './PlayersSection';

// H8: the season's players (SPEC «Игроки»): balances and turn, the inactivity hint (D-111), the flag with a
// confirmation, a correction with a comment for the log (SE3), a tech reroll by the admin, adding a player (SE4).

const t = ru.admin.players;

function player(overrides: Partial<Schemas['AdminPlayerView']>): Schemas['AdminPlayerView'] {
  return {
    id: 'p1',
    userId: 'u1',
    name: 'Вася',
    cellId: 'c2',
    points: 14,
    coins: 3,
    resources: {},
    phase: 'idle',
    playing: false,
    isInactive: false,
    lastActionAt: '2026-09-20T09:00:00+00:00',
    inactiveHint: false,
    choosingBranch: false,
    ...overrides,
  };
}

const cells = [
  { id: 'start', type: 'start' },
  { id: 'c1', type: 'empty' },
  { id: 'c2', type: 'empty' },
  { id: 'finish', type: 'finish' },
];

const ok = { duplicate: false, events: [] };

function open(players: Schemas['AdminPlayerView'][], routes: Record<string, unknown> = {}) {
  const server = fakeServer({
    'GET /api/admin/seasons/*/players': players,
    'GET /api/seasons/*': { id: seasonId, cells },
    ...routes,
  });
  render(<PlayersSection seasonId={seasonId} version={0} />);
  return server;
}

describe('The players', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows each player’s points, coins, cell and turn', async () => {
    open([player({}), player({ id: 'p2', userId: 'u2', name: 'Петя', phase: 'playing' })]);

    const vasya = await screen.findByTestId('player-p1');
    expect(vasya).toHaveTextContent(t.points(14));
    expect(vasya).toHaveTextContent(t.coins(3));
    expect(vasya).toHaveTextContent(t.cell(2));
    expect(vasya).toHaveTextContent(t.phase.idle);
    expect(within(screen.getByTestId('player-p2')).getByTestId('tech-reroll-open')).toBeVisible();
    expect(within(vasya).queryByTestId('tech-reroll-open')).toBeNull();
  });

  it('lists the players who have not acted for long, but not those already inactive', async () => {
    open([
      player({ inactiveHint: true }),
      player({ id: 'p2', userId: 'u2', name: 'Петя', inactiveHint: true, isInactive: true }),
    ]);

    const hint = await screen.findByTestId('inactive-hint');
    expect(within(hint).getByText('Вася')).toBeInTheDocument();
    expect(within(hint).queryByText('Петя')).toBeNull();
  });

  it('shows no hint block when everybody plays', async () => {
    open([player({})]);

    await screen.findByTestId('player-p1');
    expect(screen.queryByTestId('inactive-hint')).toBeNull();
  });

  it('marks a player inactive only after a confirmation with the consequences', async () => {
    const server = open([player({})], {
      'POST /api/admin/seasons/*/players/*/inactive': ok,
    });

    await userEvent.click(await screen.findByTestId('mark-inactive'));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(t.inactiveConsequences[0])).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.inactiveConfirm }));

    await waitFor(() => {
      expect(server.sent('POST', '/players/p1/inactive')[0]?.body).toMatchObject({
        isInactive: true,
      });
    });
    expect(await screen.findByText(t.markedInactive('Вася'))).toBeInTheDocument();
  });

  it('brings an inactive player back at once', async () => {
    const server = open([player({ isInactive: true })], {
      'POST /api/admin/seasons/*/players/*/inactive': ok,
    });

    await userEvent.click(await screen.findByTestId('mark-active'));

    await waitFor(() => {
      expect(server.sent('POST', '/inactive')[0]?.body).toMatchObject({ isInactive: false });
    });
  });

  it('adjusts a player: a transfer, deltas with a minus sign and the comment', async () => {
    const server = open([player({})], { 'POST /api/admin/seasons/*/players/*/adjust': ok });

    await userEvent.click(await screen.findByTestId('adjust-open'));
    const form = screen.getByTestId('adjust-form');
    const cell = within(form).getByTestId('adjust-cell');
    expect(
      within(cell)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual([
      t.noMove,
      t.cellOption(0, String(t.cellKinds.start)),
      t.cellOption(1, ''),
      t.cellOption(2, ''),
      t.cellOption(3, String(t.cellKinds.finish)),
    ]);
    await userEvent.selectOptions(cell, 'c1');
    await userEvent.type(within(form).getByTestId('adjust-points'), '−3');
    await userEvent.type(within(form).getByTestId('adjust-coins'), '5');
    await userEvent.type(within(form).getByTestId('adjust-comment'), 'Ошибка в подсчёте');
    await userEvent.click(within(form).getByTestId('adjust-submit'));

    await waitFor(() => {
      expect(server.sent('POST', '/players/p1/adjust')[0]?.body).toMatchObject({
        cellId: 'c1',
        pointsDelta: -3,
        coinsDelta: 5,
        discardOffer: false,
        comment: 'Ошибка в подсчёте',
      });
    });
    expect(await screen.findByText(t.adjusted('Вася'))).toBeInTheDocument();
  });

  it('checks the correction at its fields before sending', async () => {
    const server = open([player({})]);

    await userEvent.click(await screen.findByTestId('adjust-open'));
    const form = screen.getByTestId('adjust-form');
    await userEvent.click(within(form).getByTestId('adjust-submit'));
    expect(within(form).getByText(t.nothing)).toBeInTheDocument();
    expect(within(form).getByText(ru.admin.commentRequired)).toBeInTheDocument();

    await userEvent.type(within(form).getByTestId('adjust-points'), 'пять');
    await userEvent.click(within(form).getByTestId('adjust-submit'));
    expect(within(form).getByText(t.numberInvalid)).toBeInTheDocument();
    expect(server.sent('POST', '/adjust')).toHaveLength(0);
  });

  it('offers to discard the rolled game only while the player has one', async () => {
    open([player({}), player({ id: 'p2', userId: 'u2', name: 'Петя', phase: 'rolling' })]);

    await userEvent.click(
      within(await screen.findByTestId('player-p1')).getByTestId('adjust-open'),
    );
    expect(screen.queryByTestId('adjust-discard')).toBeNull();
    await userEvent.click(within(screen.getByTestId('player-p2')).getByTestId('adjust-open'));
    expect(screen.getByTestId('adjust-discard')).toBeInTheDocument();
  });

  it('offers to discard a branch choice at a fork, saying the steps left burn (D-305)', async () => {
    const server = open([player({ choosingBranch: true, cellId: 'c2' })], {
      'POST /api/admin/seasons/*/players/*/adjust': ok,
    });

    await userEvent.click(await screen.findByTestId('adjust-open'));
    const form = screen.getByTestId('adjust-form');
    await userEvent.click(within(form).getByRole('checkbox', { name: t.discardBranch }));
    await userEvent.type(within(form).getByTestId('adjust-comment'), 'Завис на развилке');
    await userEvent.click(within(form).getByTestId('adjust-submit'));

    expect(server.sent('POST', '/adjust')[0]?.body).toEqual(
      expect.objectContaining({ discardOffer: true }),
    );
  });

  it('shows the engine’s refusal of a correction in the form', async () => {
    open([player({})], {
      'POST /api/admin/seasons/*/players/*/adjust': () => ({
        status: 409,
        body: { title: 'Rejected', status: 409, code: 'map.transferToFinish' },
      }),
    });

    await userEvent.click(await screen.findByTestId('adjust-open'));
    const form = screen.getByTestId('adjust-form');
    await userEvent.selectOptions(within(form).getByTestId('adjust-cell'), 'finish');
    await userEvent.type(within(form).getByTestId('adjust-comment'), 'Перенос');
    await userEvent.click(within(form).getByTestId('adjust-submit'));

    expect(
      await within(form).findByText(String(ru.admin.rejection['map.transferToFinish'])),
    ).toBeInTheDocument();
  });

  it('makes a tech reroll for a playing player with a reason and a comment', async () => {
    const server = open([player({ phase: 'playing' })], {
      'POST /api/admin/seasons/*/players/*/tech-reroll': ok,
    });

    await userEvent.click(await screen.findByTestId('tech-reroll-open'));
    const form = screen.getByTestId('tech-reroll-form');
    await userEvent.selectOptions(within(form).getByTestId('tech-reroll-reason'), 'doesNotLaunch');
    await userEvent.type(within(form).getByTestId('tech-reroll-comment'), 'Вылетает на старте');
    await userEvent.click(within(form).getByTestId('tech-reroll-submit'));

    await waitFor(() => {
      expect(server.sent('POST', '/players/p1/tech-reroll')[0]?.body).toMatchObject({
        reason: 'doesNotLaunch',
        comment: 'Вылетает на старте',
      });
    });
  });

  it('adds a player from the accounts with the player role that are not in the season yet', async () => {
    const accounts = [
      { id: 'u1', login: 'vasya', name: 'Вася', role: 'player', isDeleted: false },
      { id: 'u3', login: 'sova', name: 'Сова', role: 'player', isDeleted: false },
      { id: 'u4', login: 'zritel', name: 'Зритель', role: 'spectator', isDeleted: false },
      { id: 'u5', login: 'old', name: 'Старый', role: 'player', isDeleted: true },
    ].map((a) => ({ ...a, mustChangePassword: false, createdAt: '2026-09-01T00:00:00Z' }));
    const server = open([player({})], {
      'GET /api/admin/accounts': accounts,
      'POST /api/admin/seasons/*/players': ok,
    });

    await userEvent.click(await screen.findByTestId('add-player-open'));
    const account = await screen.findByTestId('add-player-account');
    expect(
      within(account)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual([t.accountPick, 'Сова (sova)']);
    await userEvent.click(screen.getByTestId('add-player-submit'));
    expect(screen.getByText(t.accountRequired)).toBeInTheDocument();

    await userEvent.selectOptions(account, 'u3');
    await userEvent.selectOptions(screen.getByTestId('add-player-cell'), 'c1');
    await userEvent.type(screen.getByTestId('add-player-points'), '10');
    await userEvent.click(screen.getByTestId('add-player-submit'));

    await waitFor(() => {
      expect(server.sent('POST', `/seasons/${seasonId}/players`)[0]?.body).toMatchObject({
        userId: 'u3',
        cellId: 'c1',
        points: 10,
        coins: 0,
      });
    });
    expect(await screen.findByText(t.added('Сова'))).toBeInTheDocument();
  });

  it('invites to add the first player to an empty season', async () => {
    open([]);

    expect(await screen.findByText(t.emptyTitle)).toBeInTheDocument();
    expect(screen.getByTestId('add-player-open')).toBeInTheDocument();
  });
});
