import { catmullRom } from './geometry';
import type { Board, BoardCell, CellKind, Point, ZoneTheme } from './types';

// Stage 1's map is a chain of cells from the season's config (SPEC; CLAUDE.md invariant 10): the server gives the cells
// in order, without places. The board lays them as a snake across the table, row after row, the rows passing through
// the zones from the meadow at the start to the castle at the finish. Stage 2 gives real places from the content.

const step = 88;
const rowGap = 120;
const margin = 70;
const themes: ZoneTheme[] = ['meadow', 'forest', 'mountains', 'swamp', 'city', 'castle'];

/** The zones a snake of `rows` rows passes: one per row at most, the meadow first and the castle last */
function zonesFor(rows: number): ZoneTheme[] {
  if (rows >= themes.length) return themes;
  if (rows <= 1) return ['meadow'];
  const middle = themes.slice(1, -1);
  const between = Array.from(
    { length: rows - 2 },
    (_, i) => middle[Math.floor(((i + 0.5) * middle.length) / (rows - 2))],
  ).filter((t): t is ZoneTheme => t !== undefined);
  return ['meadow', ...between, 'castle'];
}

export type ChainCell = { id: string; type: 'start' | 'empty' | 'finish' };

function kindOf(type: ChainCell['type']): CellKind {
  return type === 'start' ? 'start' : type === 'finish' ? 'finish' : 'plain';
}

/** The board of a chain, and the board's number for each of the server's cell ids */
export function linearBoard(
  chain: ChainCell[],
  perRow = 8,
): { board: Board; cellNumber: Map<string, number> } {
  const count = Math.max(chain.length, 1);
  const columns = Math.min(perRow, count);
  const rows = Math.ceil(count / columns);
  const width = margin * 2 + (columns - 1) * step;
  const height = margin * 2 + (rows - 1) * rowGap;

  const spots: Point[] = chain.map((_, i) => {
    const row = Math.floor(i / columns);
    const along = i % columns;
    const column = row % 2 === 0 ? along : columns - 1 - along;
    return { x: margin + column * step, y: margin + row * rowGap };
  });
  const cells: BoardCell[] = chain.map((c, i) => ({
    ...(spots[i] as Point),
    id: i + 1,
    kind: kindOf(c.type),
  }));
  const edges = cells.slice(1).map((c, i) => ({ from: i + 1, to: c.id }));
  const roads = spots.length > 1 ? [catmullRom(spots, 8)] : [];

  // The rows are shared out among the zones in order: always the meadow at the start and the castle at the finish,
  // with as many zones between as there are rows for them
  const picked = zonesFor(rows);
  const zones = picked
    .map((theme, z) => {
      const first = Math.floor((z * rows) / picked.length);
      const last = Math.floor(((z + 1) * rows) / picked.length) - 1;
      return { theme, first, last };
    })
    .map(({ theme, first, last }) => {
      const top = margin + first * rowGap - rowGap / 2 + 6;
      const bottom = margin + last * rowGap + rowGap / 2 - 6;
      const left = 18;
      const right = width - 18;
      const mid = (top + bottom) / 2;
      return {
        id: theme,
        theme,
        name: '',
        outline: [
          { x: left + 30, y: top },
          { x: (left + right) / 2, y: top - 8 },
          { x: right - 30, y: top },
          { x: right, y: mid },
          { x: right - 30, y: bottom },
          { x: (left + right) / 2, y: bottom + 8 },
          { x: left + 30, y: bottom },
          { x: left, y: mid },
        ],
      };
    });

  return {
    board: { width, height, cells, edges, roads, zones },
    cellNumber: new Map(chain.map((c, i) => [c.id, i + 1])),
  };
}
