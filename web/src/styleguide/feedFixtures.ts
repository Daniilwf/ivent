import type { Schemas } from '../api/client';
import { emptyRefs, feedDays, withRefs } from '../feed/feedModel';
import { demoGames, demoPlayers } from './fixtures';

// Demo data of the feed, the profile and the game page (H5) for the styleguide and the visual tests: the demo season's
// made-up players with the longest nicknames and the pool's longest titles. Fixed dates: the screenshots do not age.

/** «Now» of the styleguide's feed: the day headings say «Сегодня» and «Вчера» the same way every day */
export const demoNow = new Date('2026-10-12T18:00:00Z');

const player = (i: number) => demoPlayers[i] ?? demoPlayers[0];
const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const games = demoGames.map((g, i) => ({ id: id(100 + i), title: g.title, hasPage: i !== 4 }));
const game = (i: number) => games[i] ?? games[0];
const runOf = (i: number) => id(200 + i);

const page: Pick<Schemas['FeedView'], 'players' | 'games' | 'runs'> = {
  players: demoPlayers.map((p, i) => ({
    id: p.id,
    userId: id(300 + i),
    name: p.name,
    avatar: p.avatar ? { id: `a${i}`, url: p.avatar, thumbnailUrl: p.avatar } : null,
    hasProfile: i !== 15,
    token: i,
  })),
  games,
  runs: games.map((g, i) => ({ id: runOf(i), gameId: g.id })),
};

let sequence = 1000;
function entry(
  command: string,
  at: string,
  type: string,
  data: Record<string, unknown>,
  undone = false,
): Schemas['FeedEntryView'] {
  sequence -= 1;
  return { sequence, commandId: command, occurredAt: at, type, data, author: null, undone };
}

const die = (sides: number, value: number) => ({ sides, value });

// Newest first, as the server gives them
const entries: Schemas['FeedEntryView'][] = [
  entry('c-review', '2026-10-12T15:42:00Z', 'run-reviewed', {
    runId: runOf(2),
    playerId: player(3)?.id,
    rating: 9,
    text: 'Лучшая метроидвания, в которую я играл. Боссы честные, карта — отдельное удовольствие, а Хорнет — любовь.',
  }),
  entry('c-complete', '2026-10-12T15:40:00Z', 'player-moved', {
    playerId: player(0)?.id,
    steps: 11,
    path: Array.from({ length: 11 }, (_, i) => `c${i}`),
    runId: runOf(0),
  }),
  entry('c-complete', '2026-10-12T15:40:00Z', 'points-changed', {
    playerId: player(0)?.id,
    delta: 11,
    runId: runOf(0),
  }),
  entry('c-complete', '2026-10-12T15:40:00Z', 'player-finished', {
    playerId: player(0)?.id,
    runId: runOf(0),
    order: 1,
  }),
  entry('c-complete', '2026-10-12T15:40:00Z', 'completion-rolled', {
    runId: runOf(0),
    playerId: player(0)?.id,
    dice: [die(6, 5), die(6, 4)],
    challengeDice: [die(4, 2)],
  }),
  entry('c-complete', '2026-10-12T15:40:00Z', 'run-completed', {
    runId: runOf(0),
    playerId: player(0)?.id,
    difficulty: 'hard',
    hours: 2,
  }),
  entry('c-roll', '2026-10-12T09:05:00Z', 'game-rolled', {
    playerId: player(5)?.id,
    category: 'Adventure',
    misses: [{ gameId: game(2)?.id }, { gameId: game(3)?.id }],
    gameId: game(1)?.id,
  }),
  entry(
    'c-undone',
    '2026-10-11T21:30:00Z',
    'game-rolled',
    {
      playerId: player(8)?.id,
      category: 'Horror',
      misses: [],
      gameId: game(4)?.id,
    },
    true,
  ),
  entry('c-drop', '2026-10-11T19:12:00Z', 'manual-effect-created', {
    playerId: player(1)?.id,
    drawEvent: 'bad',
    source: 'drop',
  }),
  entry('c-drop', '2026-10-11T19:12:00Z', 'player-moved', {
    playerId: player(1)?.id,
    steps: -5,
    path: ['a', 'b', 'c', 'd', 'e'],
  }),
  entry('c-drop', '2026-10-11T19:12:00Z', 'points-changed', { playerId: player(1)?.id, delta: -5 }),
  entry('c-drop', '2026-10-11T19:12:00Z', 'run-dropped', {
    runId: runOf(3),
    playerId: player(1)?.id,
    penaltyDice: [die(4, 3), die(4, 2)],
  }),
  entry('c-adjust', '2026-10-09T12:00:00Z', 'coins-changed', {
    playerId: player(15)?.id,
    delta: 3,
  }),
  entry('c-adjust', '2026-10-09T12:00:00Z', 'player-adjusted', {
    playerId: player(15)?.id,
    comment: 'Компенсация за упавший сервер',
  }),
  entry('c-season', '2026-10-01T09:00:00Z', 'season-status-changed', {
    from: 'draft',
    to: 'active',
  }),
];

/** The feed's lines: a completion with a finish, a review, a roll, an undone roll, a drop, an admin's change, the start */
export const demoFeedDays = feedDays(entries, withRefs(emptyRefs(), page), demoNow);

/** A profile with the longest nickname: two seasons, a long review and a short one */
export const demoProfile: Schemas['ProfileView'] = {
  id: id(300),
  name: player(0)?.name ?? '',
  avatar: null,
  completed: 14,
  seasons: [
    {
      seasonId: id(1),
      seasonName: 'Осенний сезон 2026',
      status: 'active',
      playerId: id(2),
      points: 61,
      place: null,
      token: 0,
    },
    {
      seasonId: id(3),
      seasonName: 'Весна 2026',
      status: 'finished',
      playerId: id(4),
      points: 44,
      place: 1,
      token: 2,
    },
  ],
  reviews: [
    {
      runId: runOf(0),
      gameId: game(0)?.id ?? '',
      gameTitle: game(0)?.title ?? '',
      seasonId: id(1),
      seasonName: 'Осенний сезон 2026',
      rating: 7,
      text: 'Абсурд от начала до конца, но проходится за вечер. Концовка стоит того, чтобы дотерпеть.',
      completedAt: '2026-10-12T15:40:00Z',
    },
    {
      runId: runOf(2),
      gameId: game(2)?.id ?? '',
      gameTitle: game(2)?.title ?? '',
      seasonId: id(3),
      seasonName: 'Весна 2026',
      rating: 10,
      text: null,
      completedAt: '2026-04-02T10:00:00Z',
    },
  ],
};

/** The same player's own page, before any season and review */
export const demoProfileEmpty: Schemas['ProfileView'] = {
  ...demoProfile,
  id: id(301),
  name: player(2)?.name ?? '',
  avatar: player(2)?.avatar
    ? { id: 'a2', url: player(2)?.avatar ?? '', thumbnailUrl: player(2)?.avatar ?? '' }
    : null,
  completed: 0,
  seasons: [],
  reviews: [],
};

/** The pool's longest title, no cover, with its runs of every kind */
export const demoGameCard: Schemas['PoolGameView'] = {
  id: game(0)?.id ?? '',
  title: game(0)?.title ?? '',
  tags: demoGames[0]?.tags ?? [],
  hours: 2,
  year: 2019,
  steamAppId: null,
  cover: null,
  note: null,
  isCoop: true,
  author: null,
  isDeleted: false,
  completionCondition: 'Любая концовка, достижения не нужны',
};

const run = (i: number, over: Partial<Schemas['GameRunView']>): Schemas['GameRunView'] => ({
  runId: runOf(10 + i),
  seasonId: id(1),
  seasonName: 'Осенний сезон 2026',
  playerId: player(i)?.id ?? '',
  userId: id(300 + i),
  playerName: player(i)?.name ?? '',
  status: 'completed',
  difficulty: 'normal',
  hours: 2.5,
  completedAt: '2026-10-12T15:40:00Z',
  rating: null,
  reviewText: null,
  token: i,
  ...over,
});

export const demoGameRuns: Schemas['GameRunView'][] = [
  run(5, { status: 'playing', difficulty: null, hours: null, completedAt: null }),
  run(0, {
    difficulty: 'hard',
    rating: 7,
    reviewText:
      'Абсурд от начала до конца, но проходится за вечер. Концовка стоит того, чтобы дотерпеть.',
  }),
  run(1, { status: 'dropped', difficulty: null, hours: null, completedAt: null }),
  run(8, { status: 'rejected', seasonName: 'Весна 2026', completedAt: '2026-04-02T10:00:00Z' }),
];
