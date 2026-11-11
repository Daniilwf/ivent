import { describe, expect, it } from 'vitest';
import { graphBoard, type GraphMap, type MapCell, type MapEdge } from './graphBoard';
import { linearBoard } from './linearBoard';

// The numbers a player navigates by (the map, the list for screen readers, the branch buttons, the admin's picker) are
// one number per cell (K1M-1): the start is 0, and the numbers grow along every way from the start (K1M-3).

const edge = (
  from: string,
  to: string,
  isDefaultForward = true,
  isPrimaryBackward = true,
): MapEdge => ({
  from,
  to,
  isDefaultForward,
  isPrimaryBackward,
});
const cell = (id: string, type: MapCell['type'] = 'empty', to?: string): MapCell => ({
  id,
  type,
  ...(to ? { to } : {}),
});

/** The hunt's map: a fork with a long branch a (a teleport back to c1) and a short branch b (a teleport into a) */
const hunt: GraphMap = {
  cells: [
    cell('start', 'start'),
    cell('c1'),
    cell('f', 'fork'),
    cell('a1', 'checkpoint'),
    cell('a2'),
    cell('a3', 'teleport', 'c1'),
    cell('a4'),
    { ...cell('a5', 'pointsBonus'), amount: 5 },
    cell('b1'),
    cell('b2', 'teleport', 'a5'),
    cell('b3'),
    cell('j'),
    cell('e1'),
    cell('finish', 'finish'),
  ],
  edges: [
    edge('start', 'c1'),
    edge('c1', 'f'),
    edge('f', 'a1'),
    edge('f', 'b1', false),
    edge('a1', 'a2'),
    edge('a2', 'a3'),
    edge('a3', 'a4'),
    edge('a4', 'a5'),
    edge('a5', 'j'),
    edge('b1', 'b2'),
    edge('b2', 'b3'),
    edge('b3', 'j', true, false),
    edge('j', 'e1'),
    edge('e1', 'finish'),
  ],
  zones: [],
};

describe('the numbers of the cells', () => {
  it('number the start 0 and the finish last', () => {
    const { cellNumber } = graphBoard(hunt);
    expect(cellNumber.get('start')).toBe(0);
    expect(cellNumber.get('finish')).toBe(hunt.cells.length - 1);
    expect(new Set(cellNumber.values()).size).toBe(hunt.cells.length);
  });

  it('grow along every arrow, so a merge comes after both branches', () => {
    const { cellNumber } = graphBoard(hunt);
    for (const e of hunt.edges)
      expect(cellNumber.get(e.to) as number, `${e.from}→${e.to}`).toBeGreaterThan(
        cellNumber.get(e.from) as number,
      );
  });

  it('number a branch in a row before the next one, the default branch first', () => {
    const { cellNumber } = graphBoard(hunt);
    expect(['a1', 'a2', 'a3', 'a4', 'a5'].map((id) => cellNumber.get(id))).toEqual([3, 4, 5, 6, 7]);
    expect(['b1', 'b2', 'b3', 'j'].map((id) => cellNumber.get(id))).toEqual([8, 9, 10, 11]);
  });

  it('give the board cells, arrows and teleports the same numbers', () => {
    const { board, cellNumber } = graphBoard(hunt);
    expect(board.cells.find((c) => c.kind === 'start')?.id).toBe(0);
    const snake = board.cells.find((c) => c.id === cellNumber.get('a3'));
    expect(snake?.to).toBe(cellNumber.get('c1'));
    for (const e of board.edges) expect(e.to).toBeGreaterThan(e.from);
  });

  it('number a chain from 0 at the start, as the admin counts it', () => {
    const chain: MapCell[] = Array.from({ length: 6 }, (_, i) => ({
      id: `c${i}`,
      type: i === 0 ? 'start' : i === 5 ? 'finish' : 'empty',
    }));
    expect([...linearBoard(chain).cellNumber.values()]).toEqual([0, 1, 2, 3, 4, 5]);
    expect(
      [
        ...graphBoard({
          cells: chain,
          edges: chain.slice(1).map((c, i) => edge(`c${i}`, c.id)),
          zones: [],
        }).cellNumber.values(),
      ].sort((a, b) => a - b),
    ).toEqual([0, 1, 2, 3, 4, 5]);
  });

  it('still numbers every cell of a draft with a ring of arrows', () => {
    const { cellNumber } = graphBoard({
      cells: [cell('start', 'start'), cell('a'), cell('b'), cell('finish', 'finish')],
      edges: [
        edge('start', 'a'),
        edge('a', 'b'),
        edge('b', 'a', true, false),
        edge('b', 'finish', false),
      ],
      zones: [],
    });
    expect([...cellNumber.values()].sort((a, b) => a - b)).toEqual([0, 1, 2, 3]);
    expect(cellNumber.get('start')).toBe(0);
  });
});
