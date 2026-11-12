import type { components } from '../api/schema';
import { graphBoard } from './graphBoard';
import { linearBoard } from './linearBoard';
import type { Board } from './types';

type SeasonMap = Pick<components['schemas']['SeasonView'], 'mapMode' | 'cells'> &
  Partial<Pick<components['schemas']['SeasonView'], 'edges' | 'zones' | 'background'>>;

/**
 * The board of a season's map and the number of each of its cells: the one numbering the player's map, the turn card,
 * the feed and the admin's picker share (K1M-1), from 0 at the start.
 */
export function seasonBoard(season: SeasonMap): { board: Board; cellNumber: Map<string, number> } {
  return season.mapMode === 'graph'
    ? graphBoard({
        cells: season.cells,
        edges: season.edges ?? [],
        zones: season.zones ?? [],
        background: season.background ?? null,
      })
    : linearBoard(season.cells);
}
