import { render, screen } from '@testing-library/react';
import type { Schemas } from '../api/client';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

vi.mock('../api/realtime', () => ({ watchSeason: () => () => {} }));

const playerId = '10000000-0000-0000-0000-000000000001';
const noDiceResult = 'Кубиков за это прохождение нет.';

function completedRun(
  overrides: Partial<Schemas['CompletedRunView']> = {},
): Schemas['CompletedRunView'] {
  return {
    id: 'b1000000-0000-0000-0000-000000000001',
    game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 12 },
    difficulty: 'normal',
    dice: [],
    challengeDice: [],
    total: 0,
    review: null,
    proof: null,
    status: 'completed',
    conditions: [
      {
        objectId: 'not-even-sweating',
        name: 'Даже не вспотел',
        kind: 'bad',
        hostile: false,
        difficultyAtLeast: 'hard',
        difficultyAtMost: null,
      },
      {
        objectId: 'keep-calm',
        name: 'Без расслабления',
        kind: 'bad',
        hostile: false,
        difficultyAtLeast: null,
        difficultyAtMost: 'extreme',
      },
    ],
    conditionCheck: { met: false, waived: false },
    ...overrides,
  };
}

function season(run: Schemas['CompletedRunView']): Schemas['SeasonView'] {
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
        points: run.total,
        phase: 'idle',
        finishOrder: null,
        avatar: null,
        token: 0,
      },
    ],
    leaderboard: [
      {
        playerId,
        place: 1,
        points: run.total,
        cellsToFinish: 1,
        isFirst: false,
        provisional: false,
      },
    ],
    me: {
      playerId,
      phase: 'idle',
      offer: null,
      choice: null,
      activeRun: null,
      lastCompleted: run,
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
    },
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    economy: { items: false, shop: false, bets: false },
  };
}

function open(run: Schemas['CompletedRunView']) {
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
    [`GET /api/seasons/${seasonId}`]: season(run),
  });
  return render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
}

describe('the actual reward after checking event conditions', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('explains an unmet completed run without any dice once beside the aggregate check', async () => {
    open(completedRun());

    const note = await screen.findByText(noDiceResult);

    expect(note).toBeVisible();
    expect(screen.getAllByText(noDiceResult)).toHaveLength(1);
    expect(note.closest('li')).toBeNull();
    const check = screen.getByText('Нарушено', { exact: true });
    expect(check.closest('li')).toBeNull();
    expect(screen.getByText('Даже не вспотел')).toBeVisible();
    expect(screen.getByText('Без расслабления')).toBeVisible();
  });

  it('does not claim there were no dice when either ordinary or challenge dice exist', async () => {
    for (const dice of [
      { dice: [{ sides: 4, value: 2 }], challengeDice: [] },
      { dice: [], challengeDice: [{ sides: 4, value: 2 }] },
    ]) {
      const view = open(completedRun({ ...dice, total: 2 }));

      expect(await screen.findByRole('heading', { name: 'Условия прохождения' })).toBeVisible();
      expect(screen.getByText('Нарушено', { exact: true })).toBeVisible();
      expect(screen.queryByText(noDiceResult)).not.toBeInTheDocument();

      view.unmount();
    }
  });

  it('does not claim completion coins were awarded for a rejected run without dice', async () => {
    open(completedRun({ status: 'rejected' }));

    expect(await screen.findByRole('heading', { name: 'Условия прохождения' })).toBeVisible();
    expect(screen.queryByText(noDiceResult)).not.toBeInTheDocument();
  });

  it('does not describe the no-dice penalty when the condition was waived or met', async () => {
    for (const [conditionCheck, label] of [
      [{ met: false, waived: true }, 'Не применимо'],
      [{ met: true, waived: false }, 'Выполнено'],
    ] as const) {
      const view = open(completedRun({ conditionCheck }));

      expect(await screen.findByRole('heading', { name: 'Условия прохождения' })).toBeVisible();
      expect(screen.getByText(label, { exact: true })).toBeVisible();
      expect(screen.queryByText(noDiceResult)).not.toBeInTheDocument();

      view.unmount();
    }
  });
});
