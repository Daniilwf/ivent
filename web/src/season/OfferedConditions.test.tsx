import { render, screen, within } from '@testing-library/react';
import type { Schemas } from '../api/client';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: unknown[]) => void) => {
    onChange([]);
    return () => {};
  },
}));

type Mode = 'offer' | 'choice';
type Game = Schemas['OfferedGameView'];
type Condition = Schemas['RunConditionView'];

const playerId = '10000000-0000-0000-0000-000000000001';
const firstGame = 'a1000000-0000-0000-0000-000000000001';
const secondGame = 'a1000000-0000-0000-0000-000000000002';

function condition(overrides: Partial<Condition> = {}): Condition {
  return {
    objectId: 'not-even-sweating',
    name: 'Даже не вспотел',
    kind: 'bad',
    hostile: false,
    difficultyAtLeast: 'hard',
    difficultyAtMost: null,
    ...overrides,
  };
}

function game(id: string, title: string, conditions?: Condition[] | null): Game {
  const card: Game = { id, title, hours: 12, marks: [] };
  return conditions === undefined ? card : { ...card, conditions };
}

function open(mode: Mode, first: Game, second: Game, eventsEnabled = true) {
  const view: Schemas['SeasonView'] = {
    id: seasonId,
    name: 'Сезон условий предложенной игры',
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'finish', type: 'finish' },
    ],
    players: [
      {
        id: playerId,
        name: 'Вася',
        cellId: 'start',
        points: 0,
        phase: 'rolling',
        finishOrder: null,
        avatar: null,
        token: 0,
      },
    ],
    leaderboard: [
      { playerId, place: 1, points: 0, cellsToFinish: 1, isFirst: false, provisional: false },
    ],
    me: {
      playerId,
      phase: 'rolling',
      offer: mode === 'offer' ? first : null,
      choice:
        mode === 'choice'
          ? {
              id: 'ee100000-0000-0000-0000-000000000001',
              kind: 'game',
              options: [
                { id: 'first', game: first },
                { id: 'second', game: second },
              ],
            }
          : null,
      activeRun: null,
      lastCompleted: null,
      nextReroll: null,
      manualEffects: [],
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
      challengesEnabled: false,
      eventsEnabled,
      roll: null,
      unchecked: null,
      finish: null,
      coins: 10,
      checks: [],
      waitingEvents: [],
      waitingConditions: [],
    },
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    economy: { items: false, shop: false, bets: false },
  };
  fakeServer({
    'GET /api/auth/me': {
      id: 'user',
      login: 'vasya',
      name: 'Вася',
      role: 'player',
      mustChangePassword: false,
      avatar: null,
    },
    [`GET /api/seasons/${seasonId}/feed`]: {
      entries: [],
      nextBefore: null,
      players: [],
      games: [],
      runs: [],
    },
    [`GET /api/seasons/${seasonId}`]: view,
  });
  render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
}

function expectBefore(first: HTMLElement, second: HTMLElement) {
  expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
}

async function ready(mode: Mode) {
  return await screen.findByTestId(mode === 'offer' ? 'start' : 'choice');
}

describe('run conditions before the starting difficulty is committed', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the single offers fixed condition before the difficulty selector and Start', async () => {
    open('offer', game(firstGame, 'Silent Hill', [condition()]), game(secondGame, 'Outlast'));
    const start = await ready('offer');
    const turn = screen.getByTestId('turn');
    const explanation = within(turn).getByText('Даже не вспотел');

    expect(explanation).toBeVisible();
    expect(within(turn).getByRole('heading', { name: 'Условия прохождения' })).toBeVisible();
    expect(turn).toHaveTextContent(/Не ниже:\s*Сложная/);
    expectBefore(explanation, screen.getByTestId('start-difficulty'));
    expectBefore(explanation, start);
    expect(start).toBeEnabled();
    expect(screen.queryByText('Выполнено')).not.toBeInTheDocument();
    expect(screen.queryByText('Нарушено')).not.toBeInTheDocument();
  });

  it('shows each game options own conditions before its choice action without merging different conditions', async () => {
    open(
      'choice',
      game(firstGame, 'Silent Hill', [condition()]),
      game(secondGame, 'Outlast', [
        condition({
          objectId: 'relax',
          name: 'Без напряжения',
          kind: null,
          difficultyAtLeast: null,
          difficultyAtMost: 'easy',
        }),
      ]),
    );
    await ready('choice');
    const first = screen.getByText('Даже не вспотел');
    const second = screen.getByText('Без напряжения');
    const firstAction = screen.getByTestId('option-first');
    const secondAction = screen.getByTestId('option-second');

    expect(first).toBeVisible();
    expect(second).toBeVisible();
    expectBefore(first, firstAction);
    expectBefore(firstAction, second);
    expectBefore(second, secondAction);
    expect(screen.getAllByText('Даже не вспотел')).toHaveLength(1);
    expect(screen.getAllByText('Без напряжения')).toHaveLength(1);
    expect(screen.getByTestId('choice')).toHaveTextContent(/Не ниже:\s*Сложная/);
    expect(screen.getByTestId('choice')).toHaveTextContent(/Не выше:\s*Лёгкая/);
    expect(firstAction).toBeEnabled();
    expect(secondAction).toBeEnabled();
  });

  it.each(
    (['offer', 'choice'] as const).flatMap((mode) =>
      (['omitted', 'null', 'empty'] as const).map((shape) => ({ mode, shape })),
    ),
  )(
    'keeps $mode usable without an empty conditions section for $shape data',
    async ({ mode, shape }) => {
      const conditions = shape === 'omitted' ? undefined : shape === 'null' ? null : [];
      open(
        mode,
        game(firstGame, 'Silent Hill', conditions),
        game(secondGame, 'Outlast', conditions),
      );
      await ready(mode);

      expect(
        screen.queryByRole('heading', { name: 'Условия прохождения' }),
      ).not.toBeInTheDocument();
      if (mode === 'offer') {
        expect(screen.getByTestId('start')).toBeEnabled();
      } else {
        expect(screen.getByTestId('option-first')).toBeEnabled();
        expect(screen.getByTestId('option-second')).toBeEnabled();
      }
    },
  );

  it.each(['offer', 'choice'] as const)(
    'hides stale conditions on the %s while events are disabled',
    async (mode) => {
      open(
        mode,
        game(firstGame, 'Silent Hill', [condition()]),
        game(secondGame, 'Outlast', [condition({ name: 'Условие второй игры' })]),
        false,
      );
      await ready(mode);

      expect(
        screen.queryByRole('heading', { name: 'Условия прохождения' }),
      ).not.toBeInTheDocument();
      expect(screen.queryByText('Даже не вспотел')).not.toBeInTheDocument();
      expect(screen.queryByText('Условие второй игры')).not.toBeInTheDocument();
    },
  );
});
