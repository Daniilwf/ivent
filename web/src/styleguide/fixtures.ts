// Demo data for the styleguide and the visual tests: made-up players (the longest nicknames are as long as the real
// table's longest: 20, 16, 15 and 14 characters), the longest titles of the pool, no covers (others' works stay out
// of the public repository). Two avatars are our own SVG drawings.
import { demoBoard } from '../board/demoBoard';
import type { GameCard } from '../board/GameCards';
import { cellsToFinish } from '../board/geometry';
import type { LeaderRow } from '../board/Leaderboard';
import type { Player } from '../board/types';

const avatars = [
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
