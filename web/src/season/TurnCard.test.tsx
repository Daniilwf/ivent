import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { SeasonScreen } from './SeasonScreen';
import { json } from '../test/fakeServer';

// Findings of the exploratory testing of stages 1 and 2, area «season» (K1S): what the player's own turn card shows and
// what an action sends along with it.

vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: unknown[]) => void) => {
    onChange([]);
    return () => {};
  },
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const me = '10000000-0000-0000-0000-000000000001';
const petya = '10000000-0000-0000-0000-000000000002';
const offered = 'a1000000-0000-0000-0000-000000000001';

type Me = Schemas['MyTurnView'];
type Run = Schemas['CompletedRunView'];

const idle: Me = {
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
  coins: 20,
  checks: [],
};

function completed(id: string, title: string, overrides: Partial<Run> = {}): Run {
  return {
    id,
    game: { id: `g-${id}`, title, hours: 6 },
    difficulty: 'normal',
    dice: [{ sides: 4, value: 2 }],
    challengeDice: [],
    total: 2,
    review: null,
    proof: null,
    status: 'completed',
    ...overrides,
  };
}

function season(
  meView: Me | null,
  overrides: Partial<Schemas['SeasonView']> = {},
): Schemas['SeasonView'] {
  const players: Schemas['PlayerView'][] = [
    {
      id: me,
      name: 'Вася',
      cellId: 'start',
      points: 0,
      phase: meView?.phase ?? 'idle',
      finishOrder: null,
      avatar: null,
      token: 0,
    },
    {
      id: petya,
      name: 'Петя',
      cellId: 'start',
      points: 0,
      phase: 'idle',
      finishOrder: null,
      avatar: null,
      token: 1,
    },
  ];
  return {
    id: seasonId,
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'c1', type: 'empty' },
      { id: 'finish', type: 'finish' },
    ],
    players,
    // Everyone at 0 shares the first place, as at the season's start
    leaderboard: players.map((p) => ({
      playerId: p.id,
      place: 1,
      points: 0,
      cellsToFinish: 2,
      isFirst: false,
      provisional: false,
    })),
    me: meView,
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    name: 'Тестовый сезон',
    ...overrides,
  };
}

const account = {
  id: 'u',
  login: 'vasya',
  name: 'Вася',
  role: 'player',
  mustChangePassword: false,
  avatar: null,
};
const emptyFeed = { entries: [], nextBefore: null, players: [], games: [], runs: [] };

type Sent = { path: string; body: Record<string, unknown> };

function serve(view: Schemas['SeasonView']) {
  const sent: Sent[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (r: Request) => {
      const path = new URL(r.url).pathname;
      if (r.method === 'GET' && path.endsWith('/api/auth/me')) return json(200, account);
      if (r.method === 'GET' && path.endsWith('/feed')) return json(200, emptyFeed);
      if (r.method === 'GET') return json(200, view);
      sent.push({ path, body: (await r.json()) as Record<string, unknown> });
      return json(200, { duplicate: false, events: [] });
    }),
  );
  return sent;
}

describe('the turn card (K1S)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('K1S-1: takes a proof for an older run waiting for the check, not only for the latest', async () => {
    const sent = serve(
      season({
        ...idle,
        lastCompleted: completed('run-2', 'dropsy'),
        checks: [completed('run-1', 'Darkwood')],
        unchecked: { count: 2, limit: 2 },
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const older = await screen.findByTestId('check-run-1');
    expect(older).toHaveTextContent('Darkwood');
    await userEvent.click(within(older).getByText(ru.turn.proofToSend));
    await userEvent.type(
      within(older).getByTestId('proof-link'),
      'https://example.com/darkwood.png',
    );
    await userEvent.click(within(older).getByTestId('proof-submit'));

    await vi.waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent[0]?.path).toBe(`/api/seasons/${seasonId}/runs/run-1/proof`);
    expect(sent[0]?.body).toMatchObject({ links: ['https://example.com/darkwood.png'] });
  });

  it('K1S-9: tells a rejection of an older run with the admin comment', async () => {
    serve(
      season({
        ...idle,
        lastCompleted: completed('run-2', 'The Evil Within 2'),
        checks: [
          completed('run-1', 'Papers, Please', {
            status: 'rejected',
            proof: {
              status: 'rejected',
              links: [],
              note: null,
              comment: 'На ссылке нет титров',
              files: [],
            },
          }),
        ],
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const rejected = await screen.findByTestId('check-run-1');
    expect(rejected).toHaveTextContent(ru.turn.checkRejected('Papers, Please'));
    expect(rejected).toHaveTextContent(ru.proof.reviewComment('На ссылке нет титров'));
    expect(within(rejected).queryByTestId('proof-form')).toBeNull();
  });

  it('K1S-3: shows my coins and says them in the paid reroll question', async () => {
    serve(
      season({
        ...idle,
        phase: 'rolling',
        offer: { id: offered, title: 'Pizza Tower', hours: 6, marks: [] },
        nextReroll: { payment: 'coins', coins: 5 },
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('my-coins')).toHaveTextContent(ru.season.coins(20));
    await userEvent.click(screen.getByTestId('reroll'));
    expect(screen.getByTestId('reroll-confirm')).toHaveTextContent(
      ru.turn.rerollConfirm('coins', 5, 20),
    );
  });

  it('K1S-11: sends the price it showed with a reroll and the game it showed with a start', async () => {
    const sent = serve(
      season({
        ...idle,
        phase: 'rolling',
        offer: { id: offered, title: 'Pizza Tower', hours: 6, marks: [] },
        nextReroll: { payment: 'freeThisRoll', coins: 0 },
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));
    await vi.waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    await vi.waitFor(() => {
      expect(screen.getByTestId('start')).toBeEnabled();
    });
    await userEvent.click(screen.getByTestId('start'));
    await vi.waitFor(() => {
      expect(sent).toHaveLength(2);
    });

    expect(sent[0]).toMatchObject({
      path: `/api/seasons/${seasonId}/reroll`,
      body: { expectedPayment: 'freeThisRoll' },
    });
    expect(sent[1]).toMatchObject({
      path: `/api/seasons/${seasonId}/start`,
      body: { gameId: offered },
    });
  });

  it('K1S-11: tells a refusal of an action from an outdated tab in Russian', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (r: Request) => {
        const path = new URL(r.url).pathname;
        if (r.method === 'GET' && path.endsWith('/api/auth/me')) return json(200, account);
        if (r.method === 'GET' && path.endsWith('/feed')) return json(200, emptyFeed);
        if (r.method === 'GET')
          return json(
            200,
            season({
              ...idle,
              phase: 'rolling',
              offer: { id: offered, title: 'Pizza Tower', hours: 6, marks: [] },
              nextReroll: { payment: 'freeThisRoll', coins: 0 },
            }),
          );
        await r.text();
        return json(409, { title: 'rejected', status: 409, detail: null, code: 'turn.stale' });
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.rejection['turn.stale']);
  });

  it('K1S-13: says a draft season has not started yet', async () => {
    serve(season(idle, { status: 'draft' }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: ru.turn.draftTitle })).toBeInTheDocument();
    expect(screen.getByText(ru.turn.draftText)).toBeInTheDocument();
    expect(screen.queryByTestId('roll')).toBeNull();
  });

  it('K1S-19: calls nobody the leader while everyone shares the first place', async () => {
    serve(season(idle));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('roll');
    expect(screen.getByText(ru.board.sheetShared(0))).toBeInTheDocument();
    expect(screen.queryByText(/Лидирует/)).toBeNull();
  });

  it('K1S-19: tells the admin outside the season apart from a spectator', async () => {
    serve(season(null));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} admin />);

    expect(await screen.findByRole('heading', { name: ru.turn.adminTitle })).toBeInTheDocument();
    expect(screen.queryByText(ru.turn.spectator)).toBeNull();
    expect(screen.getByText(ru.board.sheetNoLeader)).toBeInTheDocument();
  });
});
