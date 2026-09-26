import type { Schemas } from '../api/client';
// Demo data for the styleguide and the visual tests: made-up players (the longest nicknames are as long as the real
// table's longest: 20, 16, 15 and 14 characters), the longest titles of the pool, no covers (others' works stay out
// of the public repository). Two avatars are our own SVG drawings.
import { demoBoard } from '../board/demoBoard';
import type { GameCard } from '../board/GameCards';
import { cellsToFinish } from '../board/geometry';
import type { LeaderRow } from '../board/Leaderboard';
import type { Player } from '../board/types';

export const avatars = [
  `${import.meta.env.BASE_URL}styleguide/avatar-cat.svg`,
  `${import.meta.env.BASE_URL}styleguide/avatar-frog.svg`,
];

const names = [
  'ОченьДлинныйНикнейм1',
  'Сова',
  'Лиса',
  'Капитан_Пельмень',
  'Крот',
  'Кофейный_кракен',
  'Енот',
  'Волк',
  'СпидранМастер',
  'Выдра',
  'Филин',
  'Заяц',
  'Лось',
  'Тюлень',
  'Барсук',
  'Сокол',
];
const cells = [61, 55, 47, 44, 41, 38, 33, 31, 28, 26, 22, 19, 15, 12, 8, 5];
const points = [61, 58, 52, 49, 44, 40, 37, 33, 30, 27, 24, 21, 17, 14, 9, 6];

export const demoPlayers: Player[] = names.map((name, i) => ({
  id: `p${i}`,
  name,
  token: i,
  avatar: i === 1 || i === 2 ? avatars[i - 1] : undefined,
  cell: cells[i] ?? 1,
  points: points[i] ?? 0,
  me: i === 2,
  first: i === 0,
  inactive: i === 15,
}));

export const demoGames: GameCard[] = [
  {
    title:
      'BRAZILIAN DRUG DEALER 3: I OPENED A PORTAL TO HELL IN THE FAVELA TRYING TO REVIVE MIT AIA',
    tags: ['Action', 'Indie', 'Horror'],
    hours: 2,
  },
  {
    title: 'Secret Agent Wizard Boy and the International Crime Syndicate',
    tags: ['Adventure', 'Indie'],
    hours: 6,
  },
  { title: 'Hollow Knight', tags: ['Platformer', 'Difficult', 'Indie'], hours: 27 },
  { title: 'Dead Cells', tags: ['Rogue-like', 'Platformer'], hours: 18 },
  { title: 'Project Zomboid', tags: ['Horror', 'RPG', 'Online Co-Op'], hours: null },
];

export const demoCategories = [
  'Action',
  'Horror',
  'Platformer',
  'RPG',
  'Indie',
  'Puzzle',
  'Rogue-like',
  'Adventure',
  'Shooter',
  'Cozy',
];

function pick(test: (p: Player) => boolean): Player {
  const found = demoPlayers.find(test);
  if (!found) throw new Error('The demo players lost a role');
  return found;
}

/** The player looking at the screen, and the first finisher */
export const demoMe = pick((p) => p.me === true);
export const demoLeader = pick((p) => p.first === true);

/** The signed-in account in the shell's example: the longest nickname */
export const demoUser = {
  id: 'u-demo',
  login: 'demo',
  name: 'ОченьДлинныйНикнейм1',
  role: 'player' as const,
  mustChangePassword: false,
  avatar: null,
};

/** The leaderboard of the demo season: the first finisher on top (still provisional), the inactive one out of the race */
export const demoRows: LeaderRow[] = demoPlayers.map((player, i) => ({
  player,
  place: i + 1,
  points: player.points,
  cellsToFinish: player.inactive ? null : cellsToFinish(demoBoard, player.cell),
  isFirst: player.first === true,
  provisional: true,
}));

// The roll's result: a long title with a mark from another player, and misses of both kinds
export const demoOffer: Schemas['OfferedGameView'] = {
  id: 'offer-1',
  title: demoGames[1]?.title ?? '',
  hours: 6,
  marks: [{ playerName: 'Сова', kind: 'dropped' }],
};

export const demoRoll: Schemas['WheelRollView'] = {
  sequence: 1,
  category: 'Adventure',
  sectors: ['Action', 'Adventure', 'Horror', 'RPG'],
  misses: [
    {
      game: 'Hollow Knight',
      reason: 'completedInSeason',
      player: 'Капитан_Пельмень',
      at: '2026-10-12T09:00:00Z',
    },
    { game: 'Dead Cells', reason: 'beingPlayed', player: 'Лиса', at: null },
  ],
};

export const demoChoice: Schemas['ChoiceView'] = {
  id: 'choice-1',
  kind: 'game',
  options: [
    { id: 'o1', game: { id: 'g1', title: 'Hollow Knight', hours: 27, marks: [] } },
    {
      id: 'o2',
      game: {
        id: 'g2',
        title: demoGames[0]?.title ?? '',
        hours: 2,
        marks: [{ playerName: 'Крот', kind: 'techRerolled' }],
      },
    },
    { id: 'o3', game: { id: 'g3', title: 'Celeste', hours: null, marks: [] } },
  ],
};

/** The penalty of the default ruleset (docs/ruleset.default.json): 2d4 on points and position and a bad event */
export const demoPenalty: Schemas['DropPenaltyView'] = {
  count: 2,
  sides: 4,
  affectsPoints: true,
  affectsPosition: true,
  badEvent: true,
};
/** Other players of the season who may have seen a run */
export const demoWitnesses: Schemas['PlayerView'][] = demoPlayers.slice(1, 5).map((p) => ({
  id: p.id,
  name: p.name,
  cellId: String(p.cell),
  points: p.points,
  phase: 'idle',
  finishOrder: null,
  avatar: null,
}));
/** A proof's screenshots: our own drawings stand for them */
export const demoProofFiles: Schemas['FileLinkView'][] = avatars.map((url, i) => ({
  id: `shot-${String(i + 1)}`,
  url,
  thumbnailUrl: url,
}));

// «Что нового»: a release with notes and one without
export const demoReleases = [
  {
    version: 'v1.1.0',
    date: '2026-10-12',
    items: [
      'Колесо показывает, кто уже прошёл выпавшую игру',
      'Кубы и ход фишки после завершения игры видно на карте',
    ],
  },
  { version: 'v1.0.1', date: '2026-10-05', items: [] },
];
