import { demoGame, demoPoolGames, demoStatuses } from './demoPool';
import { activeFilters, isFree, matches, noFilter, type PoolFilter } from './poolFilter';

// H6: search and filters of the pool page, applied on the page to the whole pool (D-162)

const status = (gameId: string) => demoStatuses.find((s) => s.gameId === gameId);

function titles(filter: Partial<PoolFilter>) {
  return demoPoolGames
    .filter((g) => matches(g, status(g.id), { ...noFilter, ...filter }))
    .map((g) => g.title);
}

describe('the pool filter', () => {
  it('passes every game with no filter', () => {
    expect(titles({})).toHaveLength(demoPoolGames.length);
  });

  it('finds a title by every word of the query, case aside, like the server', () => {
    expect(titles({ query: 'SILENT' })).toEqual(['Silent Hill 2']);
    expect(titles({ query: '  witcher   hunt ' })).toEqual(['The Witcher 3: Wild Hunt']);
    expect(titles({ query: 'witcher portal' })).toEqual([]);
  });

  it('keeps the games tagged with the category, case aside', () => {
    expect(titles({ category: 'хоррор' })).toEqual(['Alan Wake', 'Dead Space', 'Silent Hill 2']);
  });

  it('sorts games by length into buckets with the bounds in the shorter one; no hours only under any', () => {
    const at = (hours: number | null) => ({ ...demoGame, hours });
    const bucket = (hours: number | null) =>
      (['short', 'medium', 'long'] as const).filter((length) =>
        matches(at(hours), undefined, { ...noFilter, length }),
      );
    expect(bucket(0.5)).toEqual(['short']);
    expect(bucket(5)).toEqual(['short']);
    expect(bucket(5.5)).toEqual(['medium']);
    expect(bucket(15)).toEqual(['medium']);
    expect(bucket(15.5)).toEqual(['long']);
    expect(bucket(null)).toEqual([]);
    expect(matches(at(null), undefined, noFilter)).toBe(true);
  });

  it('«only free» hides games taken in the season and games excluded for me, keeps dropped ones', () => {
    expect(titles({ freeOnly: true })).toEqual([
      'Baba Is You',
      'Portal 2',
      'Silent Hill 2',
      'Tetris Effect',
    ]);
  });

  it('combines the filters', () => {
    expect(titles({ category: 'Головоломка', length: 'medium', query: 'portal' })).toEqual([
      'Portal 2',
    ]);
  });

  it('counts the filters beside the search', () => {
    expect(activeFilters(noFilter)).toBe(0);
    expect(activeFilters({ ...noFilter, query: 'x' })).toBe(0);
    expect(activeFilters({ query: '', category: 'РПГ', length: 'long', freeOnly: true })).toBe(3);
  });

  it('calls a game free without a status, with a mark only, and not when taken or excluded', () => {
    expect(isFree(undefined)).toBe(true);
    expect(isFree(demoStatuses[2])).toBe(true);
    expect(isFree(demoStatuses[0])).toBe(false);
    expect(isFree(demoStatuses[1])).toBe(false);
    expect(isFree(demoStatuses[3])).toBe(false);
  });
});
