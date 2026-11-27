import { act, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

vi.mock('motion/react', async (original) => ({
  ...(await original<typeof import('motion/react')>()),
  useReducedMotion: () => false,
}));

const hubListeners = new Set<(updates: unknown[]) => void>();
let hubChange: (() => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_id: string, onChange: (updates: unknown[]) => void) => {
    hubListeners.add(onChange);
    // Each real subscription has its own connection; a new child must not replace the screen's listener.
    hubChange = () => {
      for (const notify of hubListeners) notify([]);
    };
    onChange([]);
    return () => {
      hubListeners.delete(onChange);
      if (hubListeners.size === 0) hubChange = null;
    };
  },
}));

const playerId = '10000000-0000-0000-0000-000000000001';
const eventName = 'Попутный ветер';
const eventDescription = 'В следующем броске за прохождение на один кубик больше.';
const historyLabel = (name = eventName) => `Последний ивент: «${name}»`;

function drawn(sequence: number, name = eventName): Schemas['DrawnEventView'] {
  return {
    sequence,
    deck: 'good',
    kind: 'good',
    objectId: 'tailwind',
    name,
    description: eventDescription,
    source: 'item',
  };
}

function season(lastEvent: Schemas['DrawnEventView'] | null = null): Schemas['SeasonView'] {
  return {
    id: seasonId,
    status: 'active',
    deadline: null,
    name: 'Сезон истории',
    lastSequence: lastEvent?.sequence ?? 3,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'finish', type: 'finish' },
    ],
    edges: [],
    zones: [],
    mapMode: 'linear',
    economy: { items: false, shop: false, bets: false },
    players: [
      {
        id: playerId,
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
      lastEvent,
      checks: [],
      waitingEvents: [],
      waitingConditions: [],
    },
  };
}

type RollOutcome = 'success' | 'refusal' | 'network';
function open(
  initial = season(),
  outcome: RollOutcome = 'success',
  nextDraw: Schemas['DrawnEventView'] | null = null,
  waitForCommand?: Promise<void>,
) {
  const model = { view: initial };
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
    [`POST /api/seasons/${seasonId}/roll`]: async () => {
      await waitForCommand;
      if (outcome === 'network') throw new TypeError('The request did not reach the server');
      if (outcome === 'refusal')
        return { status: 409, body: { status: 409, code: 'roll.tooManyUnchecked' } };
      const own = model.view.me;
      if (!own) throw new Error('The history fixture must contain my turn');
      model.view = {
        ...model.view,
        lastSequence: Math.max(model.view.lastSequence + 1, nextDraw?.sequence ?? 0),
        players: model.view.players.map((player) => ({ ...player, phase: 'rolling' as const })),
        me: {
          ...own,
          phase: 'rolling',
          lastEvent: nextDraw ?? own.lastEvent ?? null,
          offer: {
            id: 'a1000000-0000-0000-0000-000000000001',
            title: 'Silent Hill',
            hours: 4,
            marks: [],
          },
        },
      };
      return { body: { duplicate: false, events: [] } };
    },
  });
  render(<SeasonScreen seasonId={seasonId} role="player" onSignedOut={vi.fn()} />);
  return { model, server };
}

async function publish(model: ReturnType<typeof open>['model'], event: Schemas['DrawnEventView']) {
  const own = model.view.me;
  if (!own) throw new Error('The history fixture must contain my turn');
  model.view = {
    ...model.view,
    lastSequence: Math.max(model.view.lastSequence + 1, event.sequence),
    me: { ...own, lastEvent: event },
  };
  act(() => {
    hubChange?.();
  });
  await screen.findByText(event.description);
  expect(screen.getByTestId('drawn-event')).toHaveTextContent(event.name);
}

function foldOf(summary: HTMLElement) {
  const details = summary.closest('details');
  if (!(details instanceof HTMLDetailsElement))
    throw new Error('Event history must use the shared disclosure');
  return details;
}

describe('the last drawn event as compact history', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('loads an old event closed and lets the reader open it without replaying the draw', async () => {
    const { server } = open(season(drawn(7)));
    const summary = await screen.findByText(historyLabel());
    expect(summary).toBeVisible();
    expect(foldOf(summary)).not.toHaveAttribute('open');
    expect(screen.getByText(eventDescription)).not.toBeVisible();
    expect(screen.getByTestId('roll')).toBeVisible();

    await userEvent.click(summary);

    expect(foldOf(summary)).toHaveAttribute('open');
    expect(screen.getByText(eventDescription)).toBeVisible();
    expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
    expect(server.sent('POST', '/roll')).toHaveLength(0);
  });

  it('shows a fresh draw fully once and keeps it open through updates of the same event', async () => {
    const { model, server } = open();
    await screen.findByTestId('roll');
    await publish(model, drawn(7));
    const card = screen.getByTestId('drawn-event');
    expect(card).toHaveAttribute('data-drawing', 'true');
    await vi.waitFor(() => expect(card).toBeVisible());
    fireEvent.click(card);
    expect(card).not.toHaveAttribute('data-drawing');
    const before = server.sent('GET', `/seasons/${seasonId}`).length;

    const refreshed = { ...drawn(7), description: 'Уточнённый текст того же вытянутого ивента.' };
    await publish(model, refreshed);

    await vi.waitFor(() => {
      expect(server.sent('GET', `/seasons/${seasonId}`).length).toBeGreaterThan(before);
    });
    expect(screen.getByTestId('drawn-event')).toBeVisible();
    expect(screen.getByText(refreshed.description)).toBeVisible();
    expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
  });

  it('folds the previous fresh event only after my next command succeeds', async () => {
    const { model, server } = open();
    await screen.findByTestId('roll');
    await publish(model, drawn(7));
    fireEvent.click(screen.getByTestId('drawn-event'));

    await userEvent.click(screen.getByTestId('roll'));

    await screen.findByTestId('offer');
    expect(server.sent('POST', '/roll')).toHaveLength(1);
    const summary = await screen.findByText(historyLabel());
    expect(foldOf(summary)).not.toHaveAttribute('open');
    expect(screen.getByText(eventDescription)).not.toBeVisible();
    await userEvent.click(summary);
    expect(screen.getByText(eventDescription)).toBeVisible();
    expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
  });

  it.each(['refusal', 'network'] as const)(
    'does not fold a fresh event after a %s answer to my command',
    async (outcome) => {
      const { model, server } = open(season(), outcome);
      await screen.findByTestId('roll');
      await publish(model, drawn(7));
      fireEvent.click(screen.getByTestId('drawn-event'));

      await userEvent.click(screen.getByTestId('roll'));

      expect(await screen.findByRole('alert')).toBeVisible();
      expect(server.sent('POST', '/roll')).toHaveLength(1);
      expect(screen.getByText(eventDescription)).toBeVisible();
      expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
    },
  );

  it('keeps the fresh event open while the command is pending and folds it after acceptance', async () => {
    let accept: (() => void) | undefined;
    const pending = new Promise<void>((resolve) => {
      accept = resolve;
    });
    const { model, server } = open(season(), 'success', null, pending);
    await screen.findByTestId('roll');
    await publish(model, drawn(7));
    fireEvent.click(screen.getByTestId('drawn-event'));

    try {
      await userEvent.click(screen.getByTestId('roll'));
      expect(server.sent('POST', '/roll')).toHaveLength(1);
      expect(screen.getByTestId('roll')).toBeDisabled();
      expect(screen.getByText(eventDescription)).toBeVisible();
    } finally {
      accept?.();
    }

    await screen.findByTestId('offer');
    expect(foldOf(await screen.findByText(historyLabel()))).not.toHaveAttribute('open');
    expect(screen.getByText(eventDescription)).not.toBeVisible();
  });

  it('keeps a new event open when the successful command itself draws another sequence', async () => {
    const { model } = open(season(), 'success', drawn(9, 'Второй ветер'));
    await screen.findByTestId('roll');
    await publish(model, drawn(7));
    fireEvent.click(screen.getByTestId('drawn-event'));

    await userEvent.click(screen.getByTestId('roll'));

    await screen.findByTestId('offer');
    expect(screen.getByTestId('drawn-event')).toHaveTextContent('Второй ветер');
    expect(screen.getByTestId('drawn-event')).toHaveAttribute('data-drawing', 'true');
    await vi.waitFor(() => expect(screen.getByTestId('drawn-event')).toBeVisible());
    expect(screen.queryByText(historyLabel())).not.toBeInTheDocument();
  });

  it('preserves a reader-opened old event through a hub refresh without animating it', async () => {
    const { model, server } = open(season(drawn(7)));
    const summary = await screen.findByText(historyLabel());
    await userEvent.click(summary);
    expect(screen.getByText(eventDescription)).toBeVisible();
    const before = server.sent('GET', `/seasons/${seasonId}`).length;

    const refreshed = { ...drawn(7), description: 'Текст старого ивента обновился.' };
    await publish(model, refreshed);

    await vi.waitFor(() => {
      expect(server.sent('GET', `/seasons/${seasonId}`).length).toBeGreaterThan(before);
    });
    expect(foldOf(screen.getByText(historyLabel()))).toHaveAttribute('open');
    expect(screen.getByText(refreshed.description)).toBeVisible();
    expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
  });

  it('never folds the question I still need to answer into the historical disclosure', async () => {
    const initial = season(drawn(7));
    if (!initial.me) throw new Error('The history fixture must contain my turn');
    initial.me.choice = {
      id: 'e1000000-0000-0000-0000-000000000001',
      kind: 'effect',
      objectId: 'tailwind',
      name: eventName,
      description: eventDescription,
      prompt: 'Кому подарить кубик?',
      options: [{ id: 'self', label: 'Себе', game: null }],
    };
    open(initial);

    const question = await screen.findByTestId('event-choice');
    expect(question.closest('details')).toBeNull();
    expect(within(question).getByText(eventDescription)).toBeVisible();
    expect(within(question).getByRole('radio', { name: 'Себе' })).toBeVisible();
    expect(within(question).getByRole('button', { name: 'Ответить' })).toBeVisible();
    expect(screen.queryByText(historyLabel())).not.toBeInTheDocument();
  });

  it('folds only the event context and leaves manual tasks and the current proof outside that disclosure', async () => {
    const initial = season(drawn(7));
    if (!initial.me) throw new Error('The history fixture must contain my turn');
    initial.me.manualEffects = [
      { id: 'e2000000-0000-0000-0000-000000000001', drawEvent: 'bad', source: 'drop', irl: false },
    ];
    initial.me.lastCompleted = {
      id: 'b1000000-0000-0000-0000-000000000001',
      game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 4 },
      difficulty: 'normal',
      dice: [],
      challengeDice: [],
      total: 0,
      review: null,
      proof: null,
      status: 'completed',
    };
    open(initial);

    const tasks = await screen.findByTestId('manual-effects');
    const proof = screen.getByTestId('proof-details');
    expect(within(tasks).getByRole('button', { name: ru.effects.applied })).toBeVisible();
    expect(within(proof).getByText(ru.turn.proofToSend)).toBeVisible();
    const details = foldOf(await screen.findByText(historyLabel()));
    expect(details).not.toContainElement(tasks);
    expect(details).not.toContainElement(proof);
  });
});
