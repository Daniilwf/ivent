import type { Schemas } from '../api/client';
import type { PoolGame, SeasonGame } from './poolFilter';

// A demo pool for the styleguide and the tests: no covers (the repo holds none), every status of the season

function game(
  id: number,
  title: string,
  tags: string[],
  hours: number | null,
  more: Partial<PoolGame> = {},
): PoolGame {
  return {
    id: `9a000000-0000-0000-0000-${String(id).padStart(12, '0')}`,
    title,
    tags,
    hours,
    year: null,
    steamAppId: null,
    cover: null,
    note: null,
    isCoop: false,
    author: null,
    isDeleted: false,
    completionCondition: null,
    ...more,
  };
}

/** One game of the demo pool, for a test that needs any */
export const demoGame = game(1, 'Alan Wake', ['Хоррор', 'Экшен'], 15, {
  year: 2010,
  author: 'Петя',
});

export const demoPoolGames: PoolGame[] = [
  demoGame,
  game(2, 'Baba Is You', ['Головоломка'], 7, { year: 2019 }),
  game(3, 'Dead Space', ['Хоррор'], 11, { year: 2008 }),
  game(4, 'Portal 2', ['Головоломка'], 8.5, { year: 2011, isCoop: true }),
  game(5, 'Silent Hill 2', ['Хоррор'], 10, {
    year: 2001,
    note: 'Челлендж: концовка «In Water»',
    author: 'Маша',
  }),
  game(6, 'Tetris Effect', ['Головоломка'], null, {
    completionCondition: 'Пройти режим «Путешествие»',
  }),
  game(7, 'The Witcher 3: Wild Hunt', ['РПГ'], 51.5, { year: 2015 }),
];

const id = (n: number) => demoPoolGames[n - 1]?.id ?? '';

export const demoStatuses: SeasonGame[] = [
  {
    gameId: id(1),
    taken: 'completedInSeason',
    takenBy: 'Вася',
    completedAt: '2026-10-12T15:00:00Z',
    marks: [],
    excludedForMe: null,
  },
  {
    gameId: id(3),
    taken: 'beingPlayed',
    takenBy: 'Петя',
    completedAt: null,
    marks: [],
    excludedForMe: null,
  },
  {
    gameId: id(5),
    taken: null,
    takenBy: null,
    completedAt: null,
    marks: [{ playerName: 'Маша', kind: 'dropped' }],
    excludedForMe: null,
  },
  {
    gameId: id(7),
    taken: null,
    takenBy: null,
    completedAt: null,
    marks: [],
    excludedForMe: 'techRerolled',
  },
];

export const demoCategories: Schemas['CategoryView'][] = [
  { name: 'Головоломка', weight: 2, games: 3 },
  { name: 'РПГ', weight: 1, games: 1 },
  { name: 'Хоррор', weight: 3, games: 3 },
  { name: 'Экшен', weight: 1, games: 1 },
];
