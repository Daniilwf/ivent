import { describe, expect, it } from 'vitest';
import { cellsToFinish, walk, zoneAt } from './geometry';
import { linearBoard, type ChainCell } from './linearBoard';

const chain = (n: number): ChainCell[] =>
  Array.from({ length: n }, (_, i) => ({
    id: `c${i}`,
    type: i === 0 ? 'start' : i === n - 1 ? 'finish' : 'empty',
  }));

describe('the board of a chain of cells (stage 1)', () => {
  it('keeps the order: a walk from the start passes every cell once and ends at the finish', () => {
    const { board } = linearBoard(chain(30));
    const path = walk(board, 1, 100);
    expect(path).toEqual(Array.from({ length: 30 }, (_, i) => i + 1));
    expect(board.cells[0]?.kind).toBe('start');
    expect(board.cells.at(-1)?.kind).toBe('finish');
    expect(cellsToFinish(board, 1)).toBe(29);
  });

  it('numbers the server cells in their order', () => {
    const { cellNumber } = linearBoard(chain(5));
    expect([...cellNumber.entries()]).toEqual([
      ['c0', 1],
      ['c1', 2],
      ['c2', 3],
      ['c3', 4],
      ['c4', 5],
    ]);
  });

  it('lays neighbours next to each other, turning at the ends of the rows', () => {
    const { board } = linearBoard(chain(20), 8);
    for (let i = 1; i < board.cells.length; i++) {
      const a = board.cells[i - 1];
      const b = board.cells[i];
      expect(Math.hypot((a?.x ?? 0) - (b?.x ?? 0), (a?.y ?? 0) - (b?.y ?? 0))).toBeLessThanOrEqual(
        120,
      );
    }
  });

  it('puts every cell inside a zone, the start in the meadow and the finish in the last zone', () => {
    const { board } = linearBoard(chain(40));
    for (const c of board.cells) expect(zoneAt(board.zones, c), `cell ${c.id}`).not.toBeNull();
    expect(zoneAt(board.zones, board.cells[0] ?? { x: 0, y: 0 })?.theme).toBe('meadow');
    expect(board.zones[0]?.theme).toBe('meadow');
    expect(board.zones.at(-1)?.theme).toBe('castle');
  });

  it('draws a board of one cell', () => {
    const { board } = linearBoard(chain(1));
    expect(board.cells).toHaveLength(1);
    expect(board.roads).toHaveLength(0);
  });
});
