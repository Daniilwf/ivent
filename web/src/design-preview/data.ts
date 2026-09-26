// Data for the design previews (G2, DESIGN.md «Процесс»): a moment of the demo season — the names of its players, games
// from the demo pool (content/pool.demo.json) and the default 60-cell map. Static on purpose: the previews compare looks,
// not behaviour. Removed with the previews once a direction is chosen.

export type PreviewPlayer = {
  name: string;
  points: number;
  cell: number;
  color: string;
  first?: boolean;
  me?: boolean;
  inactive?: boolean;
};

export type PreviewFeedEntry = {
  who: string;
  what: string;
  when: string;
  dice?: number[];
};

export const cells = 60;

// 16 colours told apart with colour blindness too: the Okabe–Ito set and its darker and lighter kin
export const tokenColors = [
  '#E69F00',
  '#56B4E9',
  '#009E73',
  '#F0E442',
  '#0072B2',
  '#D55E00',
  '#CC79A7',
  '#000000',
  '#9A6A00',
  '#1F6F9F',
  '#006C4F',
  '#B8A800',
  '#004A75',
  '#8F3F00',
  '#8C4A73',
  '#6B6B6B',
] as const;

export const players: PreviewPlayer[] = [
  { name: 'Бобр', points: 61, cell: 60, color: tokenColors[0], first: true },
  { name: 'Сова', points: 58, cell: 55, color: tokenColors[1] },
  { name: 'Лиса', points: 52, cell: 47, color: tokenColors[2], me: true },
  { name: 'Рысь', points: 49, cell: 44, color: tokenColors[3] },
  { name: 'Крот', points: 44, cell: 41, color: tokenColors[4] },
  { name: 'Енот', points: 40, cell: 38, color: tokenColors[5] },
  { name: 'Волк', points: 37, cell: 33, color: tokenColors[6] },
  { name: 'Ворон', points: 33, cell: 31, color: tokenColors[7] },
  { name: 'Ёж', points: 30, cell: 28, color: tokenColors[8] },
  { name: 'Выдра', points: 27, cell: 26, color: tokenColors[9] },
  { name: 'Филин', points: 24, cell: 22, color: tokenColors[10] },
  { name: 'Заяц', points: 21, cell: 19, color: tokenColors[11] },
  { name: 'Лось', points: 17, cell: 15, color: tokenColors[12] },
  { name: 'Тюлень', points: 14, cell: 12, color: tokenColors[13] },
  { name: 'Барсук', points: 9, cell: 8, color: tokenColors[14] },
  { name: 'Сокол', points: 6, cell: 5, color: tokenColors[15], inactive: true },
];

export const me = {
  name: 'Лиса',
  game: 'Katana Zero',
  tags: ['Action', 'Indie', 'Platformer', 'Difficult'],
  hours: 5,
  startedAgo: 'вчера в 21:40',
};

export const season = {
  name: 'Демо-сезон',
  deadline: '1 октября, 21:00 МСК',
  daysLeft: 5,
};

export const feed: PreviewFeedEntry[] = [
  { who: 'Сова', what: 'прошла Cuphead на «Сложно»', when: '12 мин назад', dice: [4, 6] },
  { who: 'Бобр', what: 'финишировал первым — пруфы одобрены', when: '1 ч назад' },
  { who: 'Рысь', what: 'выкрутила Nier: Automata', when: '2 ч назад' },
  { who: 'Крот', what: 'дропнул Darkest Dungeon 2: −3 очка и клетки', when: 'вчера' },
  { who: 'Енот', what: 'оставил отзыв на OMORI: 9 из 10', when: 'вчера' },
];
