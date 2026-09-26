import { useEffect, useState } from 'react';

// Real content for the second round of previews: covers from Steam and GIF avatars from Tenor, fetched into
// public/design-preview/local/ (not in git: they are others' works, and the longest nicknames are the table's own,
// which the public repository never holds). Without that folder the previews fall back to plain stickers and
// made-up nicknames of the same length, so they still build and run anywhere.

export type Player = {
  id: string;
  name: string;
  color: string;
  /** The letter colour on the sticker: ink or white, whichever reads better on the token colour */
  ink: string;
  points: number;
  cell: number;
  gif?: string | undefined;
  me?: boolean;
  first?: boolean;
  inactive?: boolean;
};

export type Game = {
  title: string;
  tags: string[];
  hours: number | null;
  cover?: string | undefined;
  wide?: string | undefined;
};

type Covers = Record<string, { app: number; tall?: string; wide?: string }>;

const base = `${import.meta.env.BASE_URL}design-preview/local/`;

// 16 token colours told apart with colour blindness too: Okabe–Ito and its darker and lighter kin
export const tokenColors = [
  '#E69F00',
  '#56B4E9',
  '#009E73',
  '#F0E442',
  '#0072B2',
  '#D55E00',
  '#CC79A7',
  '#3B3B3B',
  '#9A6A00',
  '#1F6F9F',
  '#006C4F',
  '#B8A800',
  '#004A75',
  '#8F3F00',
  '#8C4A73',
  '#8A8A8A',
] as const;

// The longest nicknames of the table are 20, 16, 15 and 14 characters: these stand in when the local file is absent
const inkDark = '#14231b';

function luminance(hex: string) {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

const contrast = (a: number, b: number) => (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);

export function letterInk(color: string) {
  const l = luminance(color);
  return contrast(l, 1) >= contrast(l, luminance(inkDark)) ? '#ffffff' : inkDark;
}

const fallbackNicks = [
  'ОченьДлинныйНикнейм1',
  'Капитан_Пельмень',
  'Кофейный_кракен',
  'НочнойСпидраннер',
];

export const games: Game[] = [
  { title: 'Katana Zero', tags: ['Action', 'Indie', 'Platformer', 'Difficult'], hours: 5 },
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
  {
    title: 'Петька и Василий Иванович 3: Возвращение Аляски. Перезагрузка',
    tags: ['Adventure', 'Puzzle'],
    hours: 7,
  },
  {
    title: 'Milk outside a bag of milk outside a bag of milk',
    tags: ['Visual Novel', 'Indie'],
    hours: 2,
  },
  { title: 'Cuphead', tags: ['Action', 'Platformer', 'Difficult'], hours: 11 },
  { title: 'Nier: Automata', tags: ['Action', 'RPG', 'Adventure'], hours: 33 },
  { title: 'Darkest Dungeon 2', tags: ['RPG', 'Rogue-like', 'Difficult'], hours: 29 },
  { title: 'OMORI', tags: ['Adventure', 'RPG', 'Horror'], hours: 20 },
  { title: 'Hollow Knight', tags: ['Platformer', 'Difficult', 'Indie'], hours: 27 },
  { title: 'Dead Cells', tags: ['Rogue-like', 'Platformer'], hours: 18 },
  { title: 'Blasphemous 2', tags: ['Action', 'Platformer', 'Horror'], hours: 16 },
  { title: 'Project Zomboid', tags: ['Horror', 'RPG', 'Online Co-Op'], hours: null },
  { title: 'Celeste', tags: ['Platformer', 'Indie', 'Difficult'], hours: 8 },
  { title: 'Hades', tags: ['Rogue-like', 'Action'], hours: 22 },
  { title: 'Outer Wilds', tags: ['Adventure', 'Puzzle'], hours: 17 },
  { title: 'A Plague Tale: Innocence', tags: ['Adventure', 'Horror', 'Puzzle'], hours: 12 },
  { title: 'metal hellsinger', tags: ['Shooter', 'Action', 'Indie'], hours: 6 },
];

const gifs = ['cat', 'capybara', 'raccoon', 'frog'];

function makePlayers(nicks: string[]): Player[] {
  const names = [
    nicks[0],
    'Сова',
    'Лиса',
    nicks[1],
    'Крот',
    nicks[2],
    'Енот',
    'Волк',
    nicks[3],
    'Выдра',
    'Филин',
    'Заяц',
    'Лось',
    'Тюлень',
    'Барсук',
    'Сокол',
  ];
  const cells = [60, 55, 47, 44, 41, 38, 33, 31, 28, 26, 22, 19, 15, 12, 8, 5];
  const points = [61, 58, 52, 49, 44, 40, 37, 33, 30, 27, 24, 21, 17, 14, 9, 6];
  return names.map((name, i) => ({
    id: `p${i}`,
    name: name ?? `Игрок ${i + 1}`,
    color: tokenColors[i % tokenColors.length] as string,
    ink: letterInk(tokenColors[i % tokenColors.length] as string),
    points: points[i] ?? 0,
    cell: cells[i] ?? 1,
    gif: i < 4 ? `${base}gifs/${gifs[i]}.gif` : undefined,
    me: i === 2,
    first: i === 0,
    inactive: i === 15,
  }));
}

export type Content = { players: Player[]; games: Game[]; real: boolean };

let cached: Promise<Content> | null = null;

async function load(): Promise<Content> {
  try {
    const [coversAnswer, nicksAnswer] = await Promise.all([
      fetch(`${base}covers.json`),
      fetch(`${base}nicks.json`),
    ]);
    if (!coversAnswer.ok) throw new Error('no local content');
    const covers = (await coversAnswer.json()) as Covers;
    const nicks = nicksAnswer.ok
      ? ((await nicksAnswer.json()) as { nicks: string[] }).nicks
      : fallbackNicks;
    return {
      real: true,
      players: makePlayers(nicks.length >= 4 ? nicks : fallbackNicks),
      games: games.map((g) => {
        // covers.json is keyed by the pool's own titles
        const c = covers[g.title];
        return {
          ...g,
          cover: c?.tall ? `${base}covers/${c.tall}` : undefined,
          wide: c?.wide ? `${base}covers/${c.wide}` : undefined,
        };
      }),
    };
  } catch {
    return {
      real: false,
      players: makePlayers(fallbackNicks).map((p) => ({ ...p, gif: undefined })),
      games,
    };
  }
}

export function useContent(): Content | null {
  const [content, setContent] = useState<Content | null>(null);
  useEffect(() => {
    cached ??= load();
    void cached.then(setContent);
  }, []);
  return content;
}
