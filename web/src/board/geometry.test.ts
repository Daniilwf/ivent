import { describe, expect, it } from 'vitest';
import { demoBoard } from './demoBoard';
import { cellById, cellsToFinish, walk, zoneAt } from './geometry';

const start = demoBoard.cells.find((c) => c.kind === 'start');
const finish = demoBoard.cells.find((c) => c.kind === 'finish');
const forks = demoBoard.cells.filter(
  (c) => demoBoard.edges.filter((e) => e.from === c.id).length > 1,
);

describe('the demo board', () => {
  it('is a graph with one start, one finish, two forks and two checkpoints where the ways meet', () => {
    expect(start).toBeDefined();
    expect(finish).toBeDefined();
    expect(forks).toHaveLength(2);
    expect(demoBoard.cells.filter((c) => c.kind === 'checkpoint')).toHaveLength(2);
  });

  it('reaches the finish from every cell', () => {
    for (const c of demoBoard.cells)
      if (c.kind !== 'finish')
        expect(cellsToFinish(demoBoard, c.id), `cell ${c.id}`).toBeGreaterThan(0);
    expect(cellsToFinish(demoBoard, finish?.id ?? 0)).toBe(0);
  });

  it('counts the shortest way to the finish', () => {
    const fromStart = cellsToFinish(demoBoard, start?.id ?? 1);
    // Every walk that reaches the finish takes at least as many steps
    for (const prefer of ['first', 'second'] as const) {
      const path = walk(demoBoard, start?.id ?? 1, 200, prefer);
      expect(path.at(-1)).toBe(finish?.id);
      expect(path.length - 1).toBeGreaterThanOrEqual(fromStart);
    }
  });

  it('takes the way given at a fork', () => {
    const fork = forks[0];
    expect(fork).toBeDefined();
    const [first, second] = demoBoard.edges.filter((e) => e.from === fork?.id);
    expect(walk(demoBoard, fork?.id ?? 0, 1, 'first')).toEqual([fork?.id, first?.to]);
    expect(walk(demoBoard, fork?.id ?? 0, 1, 'second')).toEqual([fork?.id, second?.to]);
  });

  it('stops a walk at the finish', () => {
    expect(walk(demoBoard, finish?.id ?? 0, 5)).toEqual([finish?.id]);
  });

  it('names the zone under a point and nothing in the sea', () => {
    expect(zoneAt(demoBoard.zones, { x: 150, y: 700 })?.theme).toBe('meadow');
    expect(zoneAt(demoBoard.zones, { x: 5, y: 5 })).toBeNull();
  });

  it('refuses a cell that is not on the board', () => {
    expect(() => cellById(demoBoard, 9999)).toThrow();
  });
});
