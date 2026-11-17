import { act, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

vi.mock('motion/react', async (original) => ({
  ...(await original<typeof import('motion/react')>()),
  useReducedMotion: () => false,
}));

let hubChange: (() => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_id: string, onChange: (updates: unknown[]) => void) => {
    hubChange = () => {
      onChange([]);
    };
    onChange([]);
    return () => {
      hubChange = null;
    };
  },
}));

const me = '10000000-0000-0000-0000-000000000001';
const petya = 'ab200000-0000-0000-0000-00000000000f';
function question(id = 'question-one'): Schemas['ChoiceView'] {
  return {
    id,
    kind: 'effect',
    objectId: 'tailwind',
    name: 'Попутный ветер',
    description: 'Подари дополнительный кубик.',
    prompt: `Кому подарить кубик? ${id}`,
    options: [{ id: 'self', label: 'Себе', game: null }],
  };
}
function drawn(sequence: number): Schemas['DrawnEventView'] {
  return {
    sequence,
    deck: 'good',
    kind: 'good',
    objectId: 'tailwind',
    name: 'Попутный ветер',
    description: 'Подари дополнительный кубик.',
    source: 'cell',
  };
}

function season(): Schemas['SeasonView'] {
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
    ],
    leaderboard: [
      { playerId: me, place: 1, points: 0, cellsToFinish: 1, isFirst: false, provisional: false },
      {
        playerId: petya,
        place: 2,
        points: 0,
        cellsToFinish: 1,
        isFirst: false,
        provisional: false,
      },
    ],
    me: {
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
      eventsEnabled: true,
      roll: null,
      unchecked: null,
      finish: null,
    },
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    name: 'Тестовый сезон',
    economy: { items: false, shop: false, bets: false },
  };
}

function open(afterAnswer: Schemas['ChoiceView'] | null = null, initial = season()) {
  const model = { view: initial };
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
    [`GET /api/seasons/${seasonId}`]: () => ({ body: model.view }),
    [`POST /api/seasons/${seasonId}/choose`]: () => {
      if (!model.view.me) throw new Error('No turn in fixture');
      model.view = {
        ...model.view,
        lastSequence: model.view.lastSequence + 1,
        me: { ...model.view.me, choice: afterAnswer },
      };
      return { body: { duplicate: false, events: [] } };
    },
  });
  render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
  return model;
}
async function publish(model: ReturnType<typeof open>, sequence: number, choice = question()) {
  if (!model.view.me) throw new Error('No turn in fixture');
  model.view = {
    ...model.view,
    lastSequence: Math.max(model.view.lastSequence + 1, sequence),
    me: { ...model.view.me, lastEvent: drawn(sequence), choice },
  };
  act(() => {
    hubChange?.();
  });
  await screen.findByText(questionPrompt(choice));
  return screen.getByTestId('drawn-event');
}
function questionPrompt(choice: Schemas['ChoiceView']) {
  if (!choice.prompt) throw new Error('The draw fixture must contain a question prompt');
  return choice.prompt;
}
async function answer() {
  await userEvent.click(screen.getByRole('radio', { name: 'Себе' }));
  await userEvent.click(screen.getByRole('button', { name: /ответить/i }));
}

describe('each event draw is animated once during an open season visit', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('does not replay the skipped question draw when the answered event moves into history', async () => {
    const model = open();
    await screen.findByRole('heading', { name: 'Тестовый сезон' });
    expect(screen.queryByTestId('drawn-event')).not.toBeInTheDocument();
    const card = await publish(model, 7);
    expect(card).toHaveAttribute('data-drawing', 'true');
    fireEvent.click(card);
    expect(card).not.toHaveAttribute('data-drawing');

    await answer();

    await vi.waitFor(() =>
      expect(screen.queryByText(questionPrompt(question()))).not.toBeInTheDocument(),
    );
    expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
  });

  it('does not draw the same event again for its next suspended question', async () => {
    const second = question('question-two');
    const model = open(second);
    await screen.findByRole('heading', { name: 'Тестовый сезон' });
    const card = await publish(model, 7);
    expect(card).toHaveAttribute('data-drawing', 'true');
    fireEvent.click(card);

    await answer();

    await screen.findByText(questionPrompt(second));
    expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
  });

  it('animates a new sequence after the previous draw was shown', async () => {
    const model = open();
    await screen.findByRole('heading', { name: 'Тестовый сезон' });
    fireEvent.click(await publish(model, 7));

    const next = await publish(model, 9, question('new-event-question'));

    expect(next).toHaveAttribute('data-drawing', 'true');
  });

  it('does not animate an older historical sequence returned after the initial page baseline', async () => {
    const initial = season();
    initial.lastSequence = 20;
    const model = open(null, initial);
    await screen.findByRole('heading', { name: 'Тестовый сезон' });

    const historical = await publish(model, 7);

    expect(historical).not.toHaveAttribute('data-drawing');
  });
});
