import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

// D-222 (a deferred finding of the H3 and H4 design reviews): the offered game, each option of a choice, the game I
// play and my last completed game show the pool's cover (the thumbnail) through the one `Cover` component; a game
// without a cover keeps the neutral placeholder.

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => undefined,
}));

const me = '10000000-0000-0000-0000-000000000001';

const cover = (n: number): Schemas['FileLinkView'] => ({
  id: `f000000${n}-0000-0000-0000-000000000000`,
  url: `/api/files/f000000${n}-0000-0000-0000-000000000000`,
  thumbnailUrl: `/api/files/f000000${n}-0000-0000-0000-000000000000/thumbnail`,
});

const offered = (
  title: string,
  withCover: Schemas['FileLinkView'] | null,
): Schemas['OfferedGameView'] => ({
  id: `a1000000-0000-0000-0000-00000000000${title.length % 10}`,
  title,
  hours: 8,
  marks: [],
  cover: withCover,
});

function season(turn: Partial<Schemas['MyTurnView']>): Schemas['SeasonView'] {
  return {
    id: seasonId,
    status: 'active',
    deadline: null,
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
        phase: turn.phase ?? 'idle',
        finishOrder: null,
        avatar: null,
        token: 0,
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
      ...turn,
    },
    lastSequence: 3,
    name: 'Тестовый сезон',
  };
}

function open(view: Schemas['SeasonView']) {
  fakeServer({
    [`GET /api/seasons/${seasonId}`]: view,
    [`GET /api/seasons/${seasonId}/feed`]: {
      entries: [],
      nextBefore: null,
      players: [],
      games: [],
      runs: [],
    },
  });
  render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
}

/** The cover pictures inside a part of the page, by their address */
const pictures = (part: HTMLElement) =>
  Array.from(part.querySelectorAll('img')).map((img) => img.getAttribute('src'));

describe('Game covers on the season screen (D-222)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the offered game with its cover', async () => {
    open(season({ phase: 'rolling', offer: offered('Silent Hill', cover(1)) }));

    const offer = await screen.findByTestId('offer');
    expect(pictures(offer)).toEqual([cover(1).thumbnailUrl]);
    expect(within(offer).queryByRole('img', { name: ru.board.noCover })).not.toBeInTheDocument();
  });

  it('keeps the placeholder for an offered game without a cover', async () => {
    open(season({ phase: 'rolling', offer: offered('Outlast', null) }));

    const offer = await screen.findByTestId('offer');
    expect(pictures(offer)).toEqual([]);
    expect(within(offer).getByRole('img', { name: ru.board.noCover })).toBeInTheDocument();
  });

  it('shows each option of a choice with its own cover or the placeholder', async () => {
    const withCover = offered('Silent Hill', cover(1));
    const without = offered('Outlast', null);
    open(
      season({
        phase: 'rolling',
        choice: {
          id: 'c0000000-0000-0000-0000-000000000001',
          kind: 'game',
          options: [
            { id: 'one', game: withCover },
            { id: 'two', game: without },
          ],
        },
      }),
    );

    expect(pictures(await screen.findByTestId('option-one'))).toEqual([cover(1).thumbnailUrl]);
    const second = screen.getByTestId('option-two');
    expect(pictures(second)).toEqual([]);
    expect(within(second).getByRole('img', { name: ru.board.noCover })).toBeInTheDocument();
  });

  it('shows the game I play with its cover', async () => {
    open(
      season({
        phase: 'playing',
        activeRun: {
          id: 'b1000000-0000-0000-0000-000000000001',
          game: { id: 'a1', title: 'Alan Wake', hours: 15, cover: cover(2) },
          startedAt: '2026-10-01T12:00:00Z',
        },
      }),
    );

    expect(pictures(await screen.findByTestId('active-run'))).toEqual([cover(2).thumbnailUrl]);
  });

  it('shows my last completed game with its cover beside its dice', async () => {
    open(
      season({
        lastCompleted: {
          id: 'b1000000-0000-0000-0000-000000000002',
          game: { id: 'a2', title: 'Silent Hill', hours: 6, cover: cover(1) },
          difficulty: 'normal',
          dice: [{ sides: 4, value: 3 }],
          challengeDice: [],
          total: 3,
          review: null,
          proof: null,
          status: 'completed',
        },
      }),
    );

    const dice = await screen.findByTestId('last-dice');
    expect(pictures(dice.parentElement as HTMLElement)).toEqual([cover(1).thumbnailUrl]);
  });

  it('shows the cover of the game the wheel lands on', async () => {
    // On a desktop the landed wheel stays on the map's stage with the result
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: query.includes('min-width'),
      media: query,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }));
    let current = season({});
    fakeServer({
      [`GET /api/seasons/${seasonId}`]: () => ({ body: current }),
      [`GET /api/seasons/${seasonId}/feed`]: {
        entries: [],
        nextBefore: null,
        players: [],
        games: [],
        runs: [],
      },
      [`POST /api/seasons/${seasonId}/roll`]: () => {
        current = {
          ...season({
            phase: 'rolling',
            offer: offered('Silent Hill', cover(1)),
            nextReroll: { payment: 'freeThisRoll', coins: 0 },
            roll: { sequence: 7, category: 'Horror', sectors: ['Action', 'Horror'], misses: [] },
          }),
          lastSequence: 7,
        };
        return { body: { duplicate: false, events: [] } };
      },
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('roll'));

    await userEvent.click(
      within(await screen.findByTestId('wheel')).getByRole('button', { name: ru.moments.skip }),
    );
    expect(pictures(await screen.findByTestId('wheel'))).toContain(cover(1).thumbnailUrl);
  });
});
