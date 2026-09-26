import type { Schemas } from '../api/client';

export type PoolGame = Schemas['GameView'];
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

/** A game nobody holds in the season and that can still come to me */
export function isFree(status: SeasonGame | undefined): boolean {
  return !status || (!status.taken && !status.excludedForMe);
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
export function matches(game: PoolGame, status: SeasonGame | undefined, filter: PoolFilter) {
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
  return !filter.freeOnly || isFree(status);
}
