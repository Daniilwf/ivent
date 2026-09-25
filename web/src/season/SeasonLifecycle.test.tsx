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
      { id: me, name: 'Вася', cellId: 'start', points: 0, phase: 'idle', finishOrder: null },
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
      finish: null,
    },
    lastSequence: 3,
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
    expect(shown).toHaveTextContent('01.01.2027');
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
});
