import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { answer, fakeServer, json, seasonId, type Handler } from '../test/fakeServer';
import { InteractionsScreen } from './InteractionsScreen';

type Snapshot = Schemas['SeasonView'];
type Invitation = Schemas['InteractionView'];
type PartnerRequest = Schemas['PartnerRequestView'];
type Coop = Schemas['CoopView'];
type Duel = Schemas['DuelView'];
type Update = { seasonId: string; fromSequence: number; toSequence: number; types: string[] };

let notify: ((updates: Update[]) => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: Update[]) => void) => {
    notify = onChange;
    onChange([]);
    return () => {
      notify = null;
    };
  },
}));

const prefix = `/api/seasons/${seasonId}`;
const me = '10000000-0000-0000-0000-000000000001';
const peer = '10000000-0000-0000-0000-000000000002';
const other = '10000000-0000-0000-0000-000000000003';
const runId = '20000000-0000-0000-0000-000000000001';
const invitationId = '30000000-0000-0000-0000-000000000001';
const requestId = '40000000-0000-0000-0000-000000000001';
const createdAt = '2026-11-19T12:00:00Z';
const expiresAt = '2026-11-20T12:00:00Z';

function player(id: string, name: string, token: number): Schemas['PlayerView'] {
  return {
    id,
    name,
    cellId: 'start',
    points: 0,
    phase: 'idle',
    finishOrder: null,
    avatar: null,
    token,
  };
}

function snapshot(overrides: Partial<Snapshot> = {}): Snapshot {
  return {
    id: seasonId,
    name: 'Осенний сезон',
    status: 'active',
    deadline: null,
    lastSequence: 1,
    interactionFeatures: { coop: true, duels: true, partnerBoard: true },
    players: [player(me, 'Оля', 0), player(peer, 'Вася', 1), player(other, 'Маша', 2)],
    me: {
      playerId: me,
      phase: 'idle',
      activeRun: null,
      offer: null,
      choice: null,
      nextReroll: null,
      lastCompleted: null,
      manualEffects: [],
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
      challengesEnabled: false,
      finish: null,
      unchecked: null,
      roll: null,
    },
    cells: [],
    edges: [],
    zones: [],
    leaderboard: [],
    mapMode: 'linear',
    ...overrides,
  };
}

function invitation(overrides: Partial<Invitation> = {}): Invitation {
  return {
    id: invitationId,
    fromPlayerId: peer,
    toPlayerId: me,
    kind: 'coop',
    runId,
    status: 'pending',
    createdAt,
    expiresAt,
    ...overrides,
  };
}

function request(playerId = peer): PartnerRequest {
  return {
    id: requestId,
    playerId,
    runId,
    status: 'open',
    createdAt,
    expiresAt,
    gameTitle: 'Portal 2',
  };
}

function duel(overrides: Partial<Duel> = {}): Duel {
  return {
    id: invitationId,
    challengerId: me,
    opponentId: peer,
    acceptedAt: createdAt,
    expiresAt,
    status: 'active',
    winnerId: null,
    runId: null,
    coins: 0,
    end: null,
    endedAt: null,
    ...overrides,
  };
}

function playingCoop(): NonNullable<Snapshot['me']> {
  const turn = snapshot().me;
  if (!turn) throw new Error('The fixture has no player');
  return {
    ...turn,
    phase: 'playing',
    activeRun: {
      id: runId,
      game: {
        id: '50000000-0000-0000-0000-000000000001',
        title: 'Portal 2',
        hours: 8,
        isCoop: true,
      },
      startedAt: createdAt,
    },
  };
}

const accepted = () => json(200, { duplicate: false, events: [] });

function serve({
  season = () => snapshot(),
  invitations = () => [] as Invitation[],
  board = () => [] as PartnerRequest[],
  coop = () => null as Coop | null,
  duels = () => [] as Duel[],
  writes = {},
}: {
  season?: () => Snapshot | Response;
  invitations?: () => Invitation[];
  board?: () => PartnerRequest[];
  coop?: () => Coop | null;
  duels?: () => Duel[];
  writes?: Record<string, Handler>;
} = {}) {
  return fakeServer({
    [`GET ${prefix}`]: () => {
      const response = season();
      return response instanceof Response ? response : json(200, response);
    },
    [`GET ${prefix}/interactions`]: () => json(200, invitations()),
    [`GET ${prefix}/coop`]: () => json(200, coop()),
    [`GET ${prefix}/partner-requests`]: () => json(200, board()),
    [`GET ${prefix}/duels`]: () => json(200, duels()),
    ...writes,
  });
}

function open(role: Schemas['CurrentUser']['role'] = 'player') {
  const props = { seasonId, onSignedOut: vi.fn(), role };
  render(<InteractionsScreen {...props} />);
}

const actionId: unknown = expect.stringMatching(/^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/iu);

describe('season interactions', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('accepts an incoming coop invitation once, then shows the server’s shared run and lets the partner leave', async () => {
    let pending: Invitation[] = [invitation()];
    let shared: Coop | null = null;
    const server = serve({
      invitations: () => pending,
      coop: () => shared,
      writes: {
        [`POST ${prefix}/interactions/${invitationId}/answer`]: () => {
          pending = [invitation({ status: 'accepted' })];
          shared = { runId, rollerId: peer, partnerId: me, joinedAt: createdAt };
          return accepted();
        },
        [`POST ${prefix}/coop/leave`]: () => {
          shared = null;
          return accepted();
        },
      },
    });
    open();

    const accept = await screen.findByRole('button', { name: 'Принять' });
    expect(screen.getByText(/20 ноября.*15:00 МСК/iu)).toBeInTheDocument();
    await userEvent.dblClick(accept);
    const leave = await screen.findByRole('button', { name: 'Выйти из коопа' });
    expect(screen.queryByRole('button', { name: 'Принять' })).not.toBeInTheDocument();
    expect(server.sent('POST', `/interactions/${invitationId}/answer`)).toMatchObject([
      { body: { commandId: actionId, accept: true } },
    ]);

    await userEvent.click(leave);
    await screen.findByText('Ты вышел из коопа.');
    expect(screen.queryByRole('button', { name: 'Выйти из коопа' })).not.toBeInTheDocument();
    expect(server.sent('POST', '/coop/leave')).toMatchObject([{ body: { commandId: actionId } }]);
  });

  it('still declines and withdraws pending coop invitations after the mechanic is switched off', async () => {
    const sentId = '30000000-0000-0000-0000-000000000002';
    const turn = playingCoop();
    const server = serve({
      season: () =>
        snapshot({
          interactionFeatures: { coop: false, duels: false, partnerBoard: false },
          me: { ...turn, hasInteractionActions: true },
        }),
      invitations: () => [
        invitation(),
        invitation({ id: sentId, fromPlayerId: me, toPlayerId: other }),
      ],
      writes: {
        [`POST ${prefix}/interactions/${invitationId}/answer`]: accepted,
        [`POST ${prefix}/interactions/${sentId}/withdraw`]: accepted,
      },
    });
    open();

    await userEvent.click(await screen.findByRole('button', { name: 'Отклонить' }));
    expect(screen.queryByRole('button', { name: 'Принять' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Пригласить в кооп' })).not.toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Отозвать' }));
    expect(server.sent('POST', `/interactions/${invitationId}/answer`)).toMatchObject([
      { body: { commandId: actionId, accept: false } },
    ]);
    expect(server.sent('POST', `/interactions/${sentId}/withdraw`)).toMatchObject([
      { body: { commandId: actionId } },
    ]);
  });

  it('sends a new coop invite or a duel challenge only after selecting another player', async () => {
    const server = serve({
      season: () => snapshot({ me: playingCoop() }),
      writes: { [`POST ${prefix}/coop/invite`]: accepted, [`POST ${prefix}/duels`]: accepted },
    });
    open();

    const partner = await screen.findByRole('combobox', { name: 'Напарник' });
    const opponent = screen.getByRole('combobox', { name: 'Соперник' });
    expect(within(partner).queryByRole('option', { name: 'Оля' })).not.toBeInTheDocument();
    expect(within(opponent).queryByRole('option', { name: 'Оля' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Пригласить в кооп' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Вызвать на дуэль' })).toBeDisabled();

    await userEvent.selectOptions(partner, peer);
    await userEvent.click(screen.getByRole('button', { name: 'Пригласить в кооп' }));
    await screen.findByText('Приглашение отправлено.');
    await userEvent.selectOptions(opponent, other);
    await userEvent.click(screen.getByRole('button', { name: 'Вызвать на дуэль' }));
    await screen.findByText('Вызов на дуэль отправлен.');

    expect(server.sent('POST', '/coop/invite')).toMatchObject([
      { body: { commandId: actionId, partnerId: peer } },
    ]);
    expect(server.sent('POST', '/duels')).toMatchObject([
      { body: { commandId: actionId, opponentId: other } },
    ]);
  });

  it('keeps busy players eligible for direct coop invites and excludes inactive players and the first finisher', async () => {
    const inactive = { ...player(other, 'Маша', 2), isInactive: true };
    const firstId = '10000000-0000-0000-0000-000000000004';
    const first = { ...player(firstId, 'Даша', 3), finishOrder: 1 };
    const busy = { ...player(peer, 'Вася', 1), phase: 'playing' } as const;
    const server = serve({
      season: () =>
        snapshot({ me: playingCoop(), players: [player(me, 'Оля', 0), inactive, first, busy] }),
      writes: { [`POST ${prefix}/coop/invite`]: accepted },
    });
    open();

    const partner = await screen.findByRole('combobox', { name: 'Напарник' });
    const opponent = screen.getByRole('combobox', { name: 'Соперник' });
    expect(within(partner).getByRole('option', { name: 'Вася' })).toBeVisible();
    for (const picker of [partner, opponent]) {
      expect(within(picker).queryByRole('option', { name: 'Маша' })).not.toBeInTheDocument();
      expect(within(picker).queryByRole('option', { name: 'Даша' })).not.toBeInTheDocument();
      expect(within(picker).queryByRole('option', { name: 'Оля' })).not.toBeInTheDocument();
    }

    await userEvent.selectOptions(partner, peer);
    await userEvent.click(screen.getByRole('button', { name: 'Пригласить в кооп' }));
    await screen.findByText('Приглашение отправлено.');
    expect(server.sent('POST', '/coop/invite')).toMatchObject([
      { body: { commandId: actionId, partnerId: peer } },
    ]);
  });

  it('refreshes both pickers on inactivity changes and cannot submit an inactive selection', async () => {
    let sequence = 1;
    let isInactive = false;
    const server = serve({
      season: () => {
        const candidate = { ...player(peer, 'Вася', 1), isInactive };
        return snapshot({
          me: playingCoop(),
          lastSequence: sequence,
          players: [player(me, 'Оля', 0), candidate, player(other, 'Маша', 2)],
        });
      },
      writes: { [`POST ${prefix}/coop/invite`]: accepted, [`POST ${prefix}/duels`]: accepted },
    });
    open();

    const partner = await screen.findByRole('combobox', { name: 'Напарник' });
    const opponent = screen.getByRole('combobox', { name: 'Соперник' });
    const invite = screen.getByRole('button', { name: 'Пригласить в кооп' });
    const challenge = screen.getByRole('button', { name: 'Вызвать на дуэль' });
    await userEvent.selectOptions(partner, peer);
    await userEvent.selectOptions(opponent, peer);
    expect(invite).toBeEnabled();
    expect(challenge).toBeEnabled();

    function updateInactivity(value: boolean) {
      isInactive = value;
      sequence += 1;
      act(() =>
        notify?.([
          {
            seasonId,
            fromSequence: sequence,
            toSequence: sequence,
            types: ['player-inactivity-set'],
          },
        ]),
      );
    }

    updateInactivity(true);
    await waitFor(() => {
      for (const picker of [partner, opponent]) {
        expect(within(picker).queryByRole('option', { name: 'Вася' })).not.toBeInTheDocument();
        expect(picker).not.toHaveValue(peer);
      }
      expect(invite).toBeDisabled();
      expect(challenge).toBeDisabled();
    });
    await userEvent.click(invite);
    await userEvent.click(challenge);
    expect(server.sent('POST', '/coop/invite')).toHaveLength(0);
    expect(server.sent('POST', '/duels')).toHaveLength(0);

    vi.useFakeTimers();
    try {
      updateInactivity(false);
      await act(async () => vi.advanceTimersByTimeAsync(8_000));
    } finally {
      vi.useRealTimers();
    }
    await within(partner).findByRole('option', { name: 'Вася' });
    expect(within(opponent).getByRole('option', { name: 'Вася' })).toBeVisible();
    await userEvent.selectOptions(opponent, peer);
    await userEvent.click(challenge);
    await screen.findByText('Вызов на дуэль отправлен.');
    expect(server.sent('POST', '/duels')).toMatchObject([
      { body: { commandId: actionId, opponentId: peer } },
    ]);
  });

  it('lets a spectator read the board with game names, without personal lists or commands', async () => {
    const server = serve({ season: () => snapshot({ me: null }), board: () => [request()] });
    open('spectator');

    expect(await screen.findByText('Portal 2')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Откликнуться' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Опубликовать запрос' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Вызвать на дуэль' })).not.toBeInTheDocument();
    expect(server.sent('GET', '/interactions')).toHaveLength(0);
    expect(server.sent('GET', '/coop')).toHaveLength(0);
  });

  it('a former player now signed in as a spectator still sees public boards and races', async () => {
    const server = serve({
      season: () => snapshot({ me: playingCoop() }),
      board: () => [request()],
      duels: () => [duel()],
      writes: {
        [`GET ${prefix}/interactions`]: answer(403),
        [`GET ${prefix}/coop`]: answer(403),
      },
    });
    open('spectator');

    expect(await screen.findByText('Portal 2')).toBeVisible();
    expect(screen.getByText('Оля и Вася')).toBeVisible();
    expect(screen.queryByRole('region', { name: 'Приглашения' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Вызвать на дуэль' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Опубликовать запрос' })).not.toBeInTheDocument();
    expect(server.sent('GET', '/interactions')).toHaveLength(0);
    expect(server.sent('GET', '/coop')).toHaveLength(0);
  });

  it.each(['idle', 'playing'] as const)(
    'keeps reading and coop cleanup for the first finisher while %s',
    async (phase) => {
      const base = snapshot();
      const players = base.players.map((p) => (p.id === me ? { ...p, finishOrder: 1 } : p));
      const turn = phase === 'playing' ? playingCoop() : base.me;
      const sentId = '30000000-0000-0000-0000-000000000002';
      const server = serve({
        season: () => snapshot({ players, me: turn }),
        board: () => [request()],
        invitations: () => [
          invitation(),
          invitation({ id: sentId, fromPlayerId: me, toPlayerId: other }),
        ],
        writes: { [`POST ${prefix}/interactions/${sentId}/withdraw`]: accepted },
      });
      open();

      expect(await screen.findByText('Portal 2')).toBeVisible();
      expect(screen.getByRole('button', { name: 'Отклонить' })).toBeEnabled();
      for (const name of [
        'Принять',
        'Вызвать на дуэль',
        'Пригласить в кооп',
        'Опубликовать запрос',
        'Откликнуться',
      ]) {
        expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
      }
      await userEvent.click(screen.getByRole('button', { name: 'Отозвать' }));
      await screen.findByText('Приглашение отозвано.');
      expect(server.sent('POST', `/interactions/${sentId}/withdraw`)).toHaveLength(1);
    },
  );

  it('publishes a board request for the active coop game and closes it after the board is disabled', async () => {
    const turn = playingCoop();
    let own: PartnerRequest | null = null;
    let boardEnabled = true;
    const server = serve({
      season: () =>
        snapshot({
          interactionFeatures: { coop: true, duels: true, partnerBoard: boardEnabled },
          me: { ...turn, partnerRequest: own },
        }),
      board: () => (own ? [own] : []),
      writes: {
        [`POST ${prefix}/partner-requests`]: () => {
          own = request(me);
          return accepted();
        },
        [`POST ${prefix}/partner-requests/${requestId}/close`]: () => {
          own = null;
          return accepted();
        },
      },
    });
    open();

    await userEvent.click(await screen.findByRole('button', { name: 'Опубликовать запрос' }));
    await screen.findByRole('button', { name: 'Закрыть запрос' });
    expect(screen.queryByRole('button', { name: 'Откликнуться' })).not.toBeInTheDocument();
    boardEnabled = false;
    act(() => notify?.([{ seasonId, fromSequence: 2, toSequence: 2, types: ['ruleset-changed'] }]));
    await screen.findByText(/доска[^.]*выключена/iu);
    await userEvent.click(screen.getByRole('button', { name: 'Закрыть запрос' }));

    await screen.findByText('Запрос закрыт.');
    expect(screen.queryByRole('button', { name: 'Закрыть запрос' })).not.toBeInTheDocument();
    expect(server.sent('POST', '/partner-requests')).toHaveLength(1);
    expect(server.sent('POST', `/partner-requests/${requestId}/close`)).toMatchObject([
      { body: { commandId: actionId } },
    ]);
  });

  it('explains a board response refused by the server without hiding the request', async () => {
    const server = serve({
      board: () => [request()],
      writes: {
        [`POST ${prefix}/partner-requests/${requestId}/respond`]: answer(409, {
          code: 'partnerRequest.alreadyResponded',
        }),
      },
    });
    open();

    await userEvent.click(await screen.findByRole('button', { name: 'Откликнуться' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/уже откликнулся/iu);
    expect(screen.getByText('Portal 2')).toBeInTheDocument();
    expect(server.sent('POST', `/partner-requests/${requestId}/respond`)).toMatchObject([
      { body: { commandId: actionId } },
    ]);
  });

  it('retries a failed read after an accepted command without sending a second answer', async () => {
    let updated = false;
    let readFails = false;
    const server = serve({
      season: () => (readFails ? json(500) : snapshot()),
      invitations: () => [invitation({ status: updated ? 'declined' : 'pending' })],
      writes: {
        [`POST ${prefix}/interactions/${invitationId}/answer`]: () => {
          updated = true;
          readFails = true;
          return accepted();
        },
      },
    });
    open();

    await userEvent.click(await screen.findByRole('button', { name: 'Отклонить' }));
    const retry = await screen.findByRole('button', { name: 'Обновить' });
    expect(screen.getByRole('button', { name: 'Отклонить' })).toBeDisabled();
    readFails = false;
    await userEvent.click(retry);

    await screen.findByText('Отклонено');
    expect(screen.queryByRole('button', { name: 'Отклонить' })).not.toBeInTheDocument();
    expect(server.sent('POST', `/interactions/${invitationId}/answer`)).toHaveLength(1);
  });

  it('shows a successful board response as an outgoing invitation awaiting the author', async () => {
    let response: Invitation[] = [];
    const server = serve({
      board: () => [request()],
      invitations: () => response,
      writes: {
        [`POST ${prefix}/partner-requests/${requestId}/respond`]: () => {
          response = [invitation({ fromPlayerId: me, toPlayerId: peer })];
          return accepted();
        },
      },
    });
    open();

    await userEvent.click(await screen.findByRole('button', { name: 'Откликнуться' }));
    const invitations = within(screen.getByRole('region', { name: 'Приглашения' }));
    expect(await invitations.findByText('Кому: Вася')).toBeInTheDocument();
    expect(invitations.getByRole('button', { name: 'Отозвать' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Откликнуться' })).not.toBeInTheDocument();
    expect(server.sent('POST', `/partner-requests/${requestId}/respond`)).toHaveLength(1);
  });

  it('shows server results when a duel is settled and consumes no action on the update', async () => {
    let duelState = duel();
    const server = serve({ season: () => snapshot({ me: null }), duels: () => [duelState] });
    open('spectator');

    const duels = within(await screen.findByRole('region', { name: 'Дуэли' }));
    expect(duels.getByText('Оля и Вася')).toBeInTheDocument();
    expect(duels.getByText('Идёт')).toBeInTheDocument();
    expect(duels.getByText(/20 ноября.*15:00 МСК/iu)).toBeInTheDocument();
    duelState = duel({
      status: 'won',
      winnerId: me,
      coins: 17,
      end: 'completed',
      runId,
      endedAt: expiresAt,
    });
    act(() => notify?.([{ seasonId, fromSequence: 2, toSequence: 2, types: ['duel-settled'] }]));
    await userEvent.click(await duels.findByText('Итоги дуэлей'));
    expect(duels.getByText('Победитель — Оля.')).toBeVisible();
    expect(duels.getByText('Награда в монетках: 17.')).toBeVisible();
    expect(server.calls.filter(({ method }) => method === 'POST')).toHaveLength(0);
  });

  it('moves an invitation that the server expires into history without an answer', async () => {
    let entry = invitation({ kind: 'duel', runId: null });
    const server = serve({ invitations: () => [entry] });
    open();

    expect(await screen.findByRole('button', { name: 'Принять' })).toBeEnabled();
    entry = { ...entry, status: 'expired' };
    act(() =>
      notify?.([{ seasonId, fromSequence: 2, toSequence: 2, types: ['interaction-expired'] }]),
    );
    await userEvent.click(await screen.findByText('История приглашений'));
    expect(screen.getByText('Истекло')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Принять' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Отклонить' })).not.toBeInTheDocument();
    expect(server.calls.filter(({ method }) => method === 'POST')).toHaveLength(0);
  });

  it('batches a burst of relevant season events into one refresh per period', async () => {
    const server = serve();
    open();
    await screen.findByRole('region', { name: 'Приглашения' });
    const reads = () => server.calls.filter(({ path }) => path === prefix).length;
    expect(reads()).toBe(1);

    vi.useFakeTimers();
    function update(type: string) {
      notify?.([{ seasonId, fromSequence: 2, toSequence: 3, types: [type] }]);
    }
    await act(async () => {
      update('interaction-created');
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(reads()).toBe(2);
    await act(async () => {
      update('interaction-answered');
      await vi.advanceTimersByTimeAsync(1000);
      update('coop-joined');
      await vi.advanceTimersByTimeAsync(6999);
    });
    expect(reads()).toBe(2);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    expect(reads()).toBe(3);
  });

  it('closes writes immediately when the deadline passes before the season status updates', async () => {
    vi.useFakeTimers();
    const deadline = new Date(Date.now() + 5000).toISOString();
    const server = serve({
      season: () => snapshot({ deadline }),
      invitations: () => [invitation()],
      board: () => [request()],
    });
    open();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(screen.getByRole('button', { name: 'Принять' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Отклонить' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Откликнуться' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Вызвать на дуэль' })).toBeInTheDocument();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(5000);
    });
    for (const name of ['Принять', 'Отклонить', 'Откликнуться', 'Вызвать на дуэль']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
    expect(screen.getByText('Portal 2')).toBeVisible();
    expect(server.calls.filter(({ method }) => method === 'POST')).toHaveLength(0);
  });

  it.each([null, { coop: true, duels: false, partnerBoard: false }])(
    'does not request unavailable interactions for a spectator (%j)',
    async (interactionFeatures) => {
      const server = serve({ season: () => snapshot({ me: null, interactionFeatures }) });
      open('spectator');

      await screen.findByRole('heading', { name: 'Взаимодействия недоступны в этом сезоне' });
      expect(server.calls.map(({ path }) => path)).toEqual([prefix]);
    },
  );
});
