import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

let hubChange: (() => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: unknown[]) => void) => {
    hubChange = () => {
      onChange([]);
    };
    onChange([]);
    return () => {
      hubChange = null;
    };
  },
}));

const playerId = '10000000-0000-0000-0000-000000000001';
const gameId = 'a1000000-0000-0000-0000-000000000001';
const runId = 'b1000000-0000-0000-0000-000000000001';

const waitingEvents: Schemas['WaitingEventView'][] = [
  {
    objectId: 'tailwind',
    name: 'Попутный ветер',
    description: 'Следующий бросок за прохождение получит ещё один кубик.',
    kind: 'good',
  },
  {
    objectId: 'long-road',
    name: 'Долгая дорога',
    description: 'Следующий ролл подберёт длинную игру.',
    kind: 'bad',
  },
];

function condition(
  overrides: Partial<Schemas['RunConditionView']> = {},
): Schemas['RunConditionView'] {
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

const runConditions: Schemas['RunConditionView'][] = [
  condition(),
  condition({
    objectId: 'no-relaxing',
    name: 'Без расслабления',
    kind: null,
    difficultyAtLeast: null,
    difficultyAtMost: 'extreme',
  }),
];

function activeRun(conditions = runConditions): Schemas['RunView'] {
  return {
    id: runId,
    game: { id: gameId, title: 'Silent Hill', hours: 12 },
    startedAt: '2026-11-17T12:00:00Z',
    declaredDifficulty: 'normal',
    conditions,
  };
}

function completedRun(
  conditionCheck: Schemas['RunConditionCheckView'] | null,
  conditions = runConditions,
): Schemas['CompletedRunView'] {
  return {
    id: runId,
    game: { id: gameId, title: 'Silent Hill', hours: 12 },
    difficulty: 'normal',
    dice: [],
    challengeDice: [],
    total: 0,
    review: null,
    proof: null,
    status: 'completed',
    conditions,
    conditionCheck,
  };
}

function season(turn: Partial<Schemas['MyTurnView']> = {}): Schemas['SeasonView'] {
  return {
    id: seasonId,
    name: 'Сезон условий',
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
        phase: turn.phase ?? 'idle',
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
      eventsEnabled: true,
      roll: null,
      unchecked: null,
      finish: null,
      coins: 10,
      checks: [],
      waitingEvents: [],
      waitingConditions: [],
      ...turn,
    },
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    economy: { items: false, shop: false, bets: false },
  };
}

function open(view: Schemas['SeasonView']) {
  const model = { view };
  const server = fakeServer({
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
    [`GET /api/seasons/${seasonId}`]: () => ({ body: model.view }),
  });
  render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
  return { model, server };
}

const titles = ['Накопленные ивенты', 'Условия следующей игры', 'Условия прохождения'] as const;
const outcomeLabels = ['Выполнено', 'Нарушено', 'Не применимо'];

describe('events and run conditions on the season screen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows accumulated events and next-game conditions inside my turn in the server order', async () => {
    open(
      season({
        waitingEvents,
        waitingConditions: [
          condition(),
          condition({
            objectId: 'stay-calm',
            name: 'Спокойная игра',
            difficultyAtLeast: null,
            difficultyAtMost: 'normal',
          }),
        ],
      }),
    );
    const turn = await screen.findByTestId('turn');

    expect(within(turn).getByRole('heading', { name: titles[0] })).toBeVisible();
    expect(within(turn).getByRole('heading', { name: titles[1] })).toBeVisible();
    for (const event of waitingEvents) {
      expect(within(turn).getByText(event.name)).toBeVisible();
      await userEvent.click(within(turn).getByText(`Текст ивента: «${event.name}»`));
      expect(within(turn).getByText(event.description)).toBeVisible();
    }
    const first = within(turn).getByText('Попутный ветер');
    const second = within(turn).getByText('Долгая дорога');
    expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(turn).getByText('Даже не вспотел')).toBeVisible();
    expect(within(turn).getByText('Спокойная игра')).toBeVisible();
    expect(turn).toHaveTextContent(/Не ниже:\s*Сложная/);
    expect(turn).toHaveTextContent(/Не выше:\s*Нормальная/);
  });

  it('shows the active runs fixed conditions and bounds without a completion result', async () => {
    open(season({ phase: 'playing', activeRun: activeRun() }));
    const turn = await screen.findByTestId('turn');

    expect(within(turn).getByRole('heading', { name: 'Условия прохождения' })).toBeVisible();
    expect(within(turn).getByText('Даже не вспотел')).toBeVisible();
    expect(within(turn).getByText('Без расслабления')).toBeVisible();
    expect(turn).toHaveTextContent(/Не ниже:\s*Сложная/);
    expect(turn).toHaveTextContent(/Не выше:\s*Выше сложной/);
    for (const label of outcomeLabels) expect(screen.queryByText(label)).not.toBeInTheDocument();
  });

  it('puts the current roll before waiting information and the previous run history after it', async () => {
    open(
      season({
        waitingEvents,
        waitingConditions: runConditions,
        lastEvent: {
          sequence: 2,
          deck: 'good',
          kind: 'good',
          objectId: 'previous-event',
          name: 'Ивент предыдущего хода',
          description: 'Ивент вытянут после предыдущего прохождения.',
          source: 'cell',
        },
        lastCompleted: {
          ...completedRun(null, []),
          dice: [{ sides: 4, value: 2 }],
          total: 2,
        },
      }),
    );
    const turn = await screen.findByTestId('turn');
    const roll = within(turn).getByTestId('roll');
    const history = within(turn).getByTestId('after');
    expect(history).toHaveTextContent('Silent Hill');

    for (const title of ['Накопленные ивенты', 'Условия следующей игры']) {
      const waiting = within(turn).getByRole('heading', { name: title });
      expect(roll.compareDocumentPosition(waiting) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(
        waiting.compareDocumentPosition(history) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy();
    }
  });

  it.each([
    [{ met: false, waived: false }, 'Нарушено'],
    [{ met: true, waived: true }, 'Не применимо'],
  ] as const)(
    'shows one aggregate result of the last completed run for %j',
    async (check, label) => {
      open(season({ lastCompleted: completedRun(check) }));
      const turn = await screen.findByTestId('turn');

      expect(within(turn).getByRole('heading', { name: 'Условия прохождения' })).toBeVisible();
      expect(within(turn).getByText('Даже не вспотел')).toBeVisible();
      expect(within(turn).getByText('Без расслабления')).toBeVisible();
      expect(within(turn).getAllByText(label, { exact: true })).toHaveLength(1);
      for (const other of outcomeLabels.filter((text) => text !== label)) {
        expect(within(turn).queryByText(other, { exact: true })).not.toBeInTheDocument();
      }
    },
  );

  it('hides all event-condition sections when events are off even if stale data is still present', async () => {
    open(
      season({
        eventsEnabled: false,
        waitingEvents,
        waitingConditions: runConditions,
        phase: 'playing',
        activeRun: activeRun(),
        lastCompleted: completedRun({ met: false, waived: false }),
      }),
    );
    await screen.findByTestId('turn');

    for (const title of titles) {
      expect(screen.queryByRole('heading', { name: title })).not.toBeInTheDocument();
    }
    expect(screen.queryByText('Попутный ветер')).not.toBeInTheDocument();
    expect(screen.queryByText('Даже не вспотел')).not.toBeInTheDocument();
  });

  it('hides empty sections while events are on', async () => {
    open(
      season({
        phase: 'playing',
        activeRun: activeRun([]),
        lastCompleted: completedRun(null, []),
      }),
    );
    await screen.findByTestId('turn');

    for (const title of titles) {
      expect(screen.queryByRole('heading', { name: title })).not.toBeInTheDocument();
    }
  });

  it('hides already visible waiting sections when a hub refresh turns events off', async () => {
    const { model, server } = open(season({ waitingEvents, waitingConditions: runConditions }));
    expect(await screen.findByRole('heading', { name: 'Накопленные ивенты' })).toBeVisible();
    await vi.waitFor(() => {
      expect(hubChange).not.toBeNull();
    });
    const current = model.view.me;
    if (!current) throw new Error('The fixture must contain my turn.');
    model.view = { ...model.view, lastSequence: 4, me: { ...current, eventsEnabled: false } };

    act(() => hubChange?.());

    await vi.waitFor(() => {
      expect(server.sent('GET', `/seasons/${seasonId}`).length).toBeGreaterThan(1);
      expect(screen.queryByRole('heading', { name: 'Накопленные ивенты' })).not.toBeInTheDocument();
      expect(
        screen.queryByRole('heading', { name: 'Условия следующей игры' }),
      ).not.toBeInTheDocument();
    });
  });
});
