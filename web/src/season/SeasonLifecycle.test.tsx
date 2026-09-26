import { render, screen } from '@testing-library/react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { SeasonScreen } from './SeasonScreen';

// The season lifecycle on the season screen (C10, SE1, SE2, D-101): the deadline in Moscow time with an explicit «МСК»
// label; in Closing a note that rolls are closed and proofs are still accepted, in Finished a note that the season is
// over; no roll button once the season is closing or finished. The texts come from ru.season (keys added with the screen).

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => {},
}));

const texts = () => ru.season;

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const me = '10000000-0000-0000-0000-000000000001';

// 21:00 UTC on 31 December is midnight of 1 January in Moscow (UTC+3)
const deadline = '2026-12-31T21:00:00+00:00';

function season(
  status: 'active' | 'closing' | 'finished',
  withDeadline = true,
): Schemas['SeasonView'] {
  return {
    id: seasonId,
    status,
    deadline: withDeadline ? deadline : null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'c1', type: 'empty' },
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
      },
    ],
    leaderboard: [
      { playerId: me, place: 1, points: 0, cellsToFinish: 2, isFirst: false, provisional: false },
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
      roll: null,
      unchecked: null,
      finish: null,
    },
    lastSequence: 3,
    name: 'Тестовый сезон',
  };
}

function serve(body: Schemas['SeasonView']) {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

describe('Season lifecycle', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the deadline in Moscow time with the «МСК» label', async () => {
    serve(season('active'));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const shown = await screen.findByTestId('season-deadline');

    expect(shown).toHaveTextContent('МСК');
    expect(shown).toHaveTextContent('00:00');
    expect(shown).toHaveTextContent('1 января');
    expect(shown).not.toHaveTextContent('21:00');
  });

  it('shows no deadline when the season has none', async () => {
    serve(season('active', false));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('roll');

    expect(screen.queryByTestId('season-deadline')).toBeNull();
  });

  it('offers the roll while the season is active and says nothing about its status', async () => {
    serve(season('active'));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('roll')).toBeEnabled();
    expect(screen.queryByTestId('season-status')).toBeNull();
  });

  it('in closing says rolls are closed and proofs are accepted, without the roll button', async () => {
    serve(season('closing'));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const note = await screen.findByTestId('season-status');

    expect(note).toHaveTextContent(texts().closing);
    expect(screen.queryByTestId('roll')).toBeNull();
  });

  it('when finished says the season is over, without the roll button', async () => {
    serve(season('finished'));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const note = await screen.findByTestId('season-status');

    expect(note).toHaveTextContent(texts().finished);
    expect(screen.queryByTestId('roll')).toBeNull();
    expect(screen.getByTestId('leaderboard')).toBeInTheDocument();
  });

  // The reviews of C10: no turn action the server would refuse, in any phase, once the turns are over.
  const offered = {
    id: 'a1000000-0000-0000-0000-000000000001',
    title: 'Silent Hill',
    hours: 12,
    marks: [],
  };
  const playing = {
    id: 'e0000000-0000-0000-0000-000000000001',
    game: { id: offered.id, title: 'Silent Hill', hours: 12 },
    startedAt: '2026-09-24T10:00:00Z',
  };
  function inPhase(
    status: 'active' | 'closing' | 'finished' | 'archived',
    phase: 'rolling' | 'playing',
    deadlineAt: string | null = deadline,
  ): Schemas['SeasonView'] {
    const base = season(status === 'archived' ? 'finished' : status);
    const me = base.me;
    if (!me) throw new Error('The fixture has a player.');
    return {
      ...base,
      status,
      deadline: deadlineAt,
      me: {
        ...me,
        phase,
        offer: phase === 'rolling' ? offered : null,
        activeRun: phase === 'playing' ? playing : null,
        nextReroll: phase === 'rolling' ? { payment: 'freeThisRoll', coins: 0 } : null,
      },
    };
  }

  it.each(['closing', 'finished', 'archived'] as const)(
    'in %s offers no start, reroll or «уже проходил» for an offered game',
    async (status) => {
      serve(inPhase(status, 'rolling'));
      render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

      await screen.findByTestId('season-status');

      expect(screen.queryByTestId('start')).toBeNull();
      expect(screen.queryByTestId('already-played')).toBeNull();
      expect(screen.queryByTestId('reroll')).toBeNull();
    },
  );

  it.each(['closing', 'finished', 'archived'] as const)(
    'in %s shows the run left playing but offers no completion, drop or tech reroll',
    async (status) => {
      serve(inPhase(status, 'playing'));
      render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

      expect(await screen.findByTestId('active-run')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: ru.turn.complete })).toBeNull();
      expect(screen.queryByTestId('drop')).toBeNull();
      expect(screen.queryByTestId('tech-reroll')).toBeNull();
    },
  );

  it('shows the finished note for an archived season', async () => {
    serve(inPhase('archived', 'rolling'));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('season-status')).toHaveTextContent(texts().finished);
  });

  it('closes the turns once the deadline has passed, before the scheduler closes the season', async () => {
    const past = new Date(Date.now() - 60_000).toISOString();
    serve({ ...season('active'), deadline: past });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('season-status')).toHaveTextContent(texts().closing);
    expect(screen.queryByTestId('roll')).toBeNull();
  });
});
