import { render, screen, within } from '@testing-library/react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { SeasonScreen } from './SeasonScreen';

// The leaderboard of the season screen (C9b, P11, D-100): rows in the order the server sends in `leaderboard` — places
// come from the engine, the screen never re-sorts by points. Each row is the place, the name, the points and the cells
// to the finish; the first finisher is marked, «предварительно» while his finish is provisional.

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => {},
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const vasya = '10000000-0000-0000-0000-000000000001';
const petya = '10000000-0000-0000-0000-000000000002';
const masha = '10000000-0000-0000-0000-000000000003';

type Row = Schemas['LeaderboardRowView'];

const names: Record<string, string> = { [vasya]: 'Вася', [petya]: 'Петя', [masha]: 'Маша' };

function season(leaderboard: Row[]): Schemas['SeasonView'] {
  return {
    id: seasonId,
    // D-101: the season's status and deadline (none here)
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'c1', type: 'empty' },
      { id: 'finish', type: 'finish' },
    ],
    // Players in id order, Петя far ahead by points: the leaderboard, not the points, decides the order
    players: [
      { id: vasya, name: 'Вася', cellId: 'finish', points: 4, phase: 'idle', finishOrder: 1 },
      { id: petya, name: 'Петя', cellId: 'c1', points: 50, phase: 'idle', finishOrder: null },
      { id: masha, name: 'Маша', cellId: 'start', points: 7, phase: 'idle', finishOrder: null },
    ],
    me: {
      playerId: vasya,
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
      finish: { order: 1, frozen: false },
    },
    lastSequence: 3,
    leaderboard,
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

function row(
  playerId: string,
  place: number,
  points: number,
  cellsToFinish: number | null,
  first: 'provisional' | 'final' | null = null,
): Row {
  return {
    playerId,
    place,
    points,
    cellsToFinish,
    isFirst: first !== null,
    provisional: first === 'provisional',
  };
}

/** The text the screen shows for a row (D-100): the row, then the first-place mark. */
function text(r: Row): string {
  const base = ru.leaderboard.row(r.place, names[r.playerId] ?? '', r.points, r.cellsToFinish);
  if (!r.isFirst) return base;
  return `${base} ${r.provisional ? ru.leaderboard.provisional : ru.leaderboard.first}`;
}

async function items(): Promise<HTMLElement[]> {
  const list = await screen.findByTestId('leaderboard');
  return within(list).getAllByRole('listitem');
}

const firstOnTop: Row[] = [
  row(vasya, 1, 4, 0, 'provisional'),
  row(petya, 2, 50, 1),
  row(masha, 3, 7, 2),
];

describe('Leaderboard', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('keeps the order of the leaderboard from the server, not the points', async () => {
    serve(season(firstOnTop));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const shown = await items();

    expect(shown.map((i) => i.dataset.testid)).toEqual([
      `leader-${vasya}`,
      `leader-${petya}`,
      `leader-${masha}`,
    ]);
  });

  it('shows the place, the name, the points and the cells to the finish of each row', async () => {
    serve(season(firstOnTop));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const shown = await items();

    expect(shown.map((i) => i.textContent)).toEqual(firstOnTop.map(text));
  });

  it('marks the first finisher as provisional and nobody else', async () => {
    serve(season(firstOnTop));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const [first, ...rest] = await items();

    expect(first).toHaveTextContent(ru.leaderboard.provisional);
    for (const other of rest) {
      expect(other).not.toHaveTextContent(ru.leaderboard.first);
    }
  });

  it('drops «provisional» once the first place is final', async () => {
    const final = [row(vasya, 1, 4, 0, 'final'), row(petya, 2, 50, 1), row(masha, 3, 7, 2)];
    serve(season(final));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const [first] = await items();

    expect(first?.textContent).toBe(text(row(vasya, 1, 4, 0, 'final')));
    expect(first).not.toHaveTextContent(ru.leaderboard.provisional);
  });

  it('shows shared places as the server sends them', async () => {
    // Without a finisher: Петя 1, Маша and Вася share 2 (the server skips place 3)
    const shared = [row(petya, 1, 50, 1), row(masha, 2, 7, 2), row(vasya, 2, 7, 0)];
    serve(season(shared));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const shown = await items();

    expect(shown.map((i) => i.textContent)).toEqual(shared.map(text));
    for (const item of shown) {
      expect(item).not.toHaveTextContent(ru.leaderboard.first);
    }
  });

  it('shows no cells to the finish when there is no way to it', async () => {
    const lost = [row(petya, 1, 50, null), row(masha, 2, 7, 2), row(vasya, 3, 4, 0)];
    serve(season(lost));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const [first] = await items();

    expect(first?.textContent).toBe(ru.leaderboard.row(1, 'Петя', 50, null));
  });
});
