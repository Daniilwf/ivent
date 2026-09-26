import type { Schemas } from '../api/client';

export type PoolGame = Schemas['PoolGameView'];
export type SeasonGame = Schemas['SeasonGameView'];

/** Length buckets of the filter by HowLongToBeat hours: a way to look through the pool, not a rule of the game */
export type LengthFilter = 'any' | 'short' | 'medium' | 'long';

export const lengthFilters: LengthFilter[] = ['any', 'short', 'medium', 'long'];

const shortUpTo = 5;
const mediumUpTo = 15;

export type PoolFilter = {
  query: string;
  /** A category's name; empty for every category */
  category: string;
  length: LengthFilter;
  /** Only games the wheel can give me now: not taken in the season and not excluded for me */
  freeOnly: boolean;
};

export const noFilter: PoolFilter = { query: '', category: '', length: 'any', freeOnly: false };

/** How many filters beside the search are on: the number on the phone's «Фильтры» button */
export function activeFilters(filter: PoolFilter): number {
  return (filter.category ? 1 : 0) + (filter.length === 'any' ? 0 : 1) + (filter.freeOnly ? 1 : 0);
}

/** Names of the wheel's categories that can come up (weight above zero), lower-cased for comparing */
export function wheelOf(categories: readonly { name: string; weight: number }[]): Set<string> {
  return new Set(
    categories.filter((c) => c.weight > 0).map((c) => c.name.toLocaleLowerCase('ru-RU')),
  );
}

/** Whether the wheel can land on the game at all: one of its tags is a category with weight (D-92) */
export function onWheel(game: PoolGame, wheel: ReadonlySet<string>): boolean {
  return game.tags.some((tag) => wheel.has(tag.toLocaleLowerCase('ru-RU')));
}

/** A game on the wheel that nobody holds in the season and that can still come to me */
export function isFree(status: SeasonGame | undefined, inWheel = true): boolean {
  return inWheel && (!status || (!status.taken && !status.excludedForMe));
}

function inLength(hours: number | null, length: LengthFilter): boolean {
  if (length === 'any') return true;
  if (hours === null) return false;
  if (length === 'short') return hours <= shortUpTo;
  if (length === 'medium') return hours > shortUpTo && hours <= mediumUpTo;
  return hours > mediumUpTo;
}

/**
 * Whether a game passes the filter. The search is like the server's: every word of the query is somewhere in the title,
 * case aside; a category matches a tag case aside.
 */
export function matches(
  game: PoolGame,
  status: SeasonGame | undefined,
  filter: PoolFilter,
  wheel?: ReadonlySet<string>,
) {
  const title = game.title.toLocaleLowerCase('ru-RU');
  const words = filter.query.toLocaleLowerCase('ru-RU').split(/\s+/).filter(Boolean);
  if (!words.every((word) => title.includes(word))) return false;
  if (
    filter.category &&
    !game.tags.some(
      (tag) => tag.toLocaleLowerCase('ru-RU') === filter.category.toLocaleLowerCase('ru-RU'),
    )
  )
    return false;
  if (!inLength(game.hours, filter.length)) return false;
  return !filter.freeOnly || isFree(status, wheel ? onWheel(game, wheel) : true);
}
