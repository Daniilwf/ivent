import { ru } from '../i18n/ru';
import { catmullRom, evenly } from './geometry';
import type { Board, BoardCell, BoardEdge, CellKind, Point, ZoneTheme } from './types';

// The demo world (G2's map, D-130): two forks that meet again on checkpoints, over six zones. The styleguide, the
// visual tests and the demo season draw it; a real season's board comes from its content (stage 2).

type Segment = { from: string; to: string; points: Point[]; cells: number };

const segments: Segment[] = [
  {
    from: 'start',
    to: 'fork1',
    cells: 11,
    points: [
      { x: 90, y: 700 },
      { x: 210, y: 700 },
      { x: 310, y: 650 },
      { x: 350, y: 560 },
      { x: 320, y: 470 },
      { x: 390, y: 400 },
    ],
  },
  // The forest: longer, calmer
  {
    from: 'fork1',
    to: 'merge1',
    cells: 11,
    points: [
      { x: 390, y: 400 },
      { x: 290, y: 340 },
      { x: 230, y: 250 },
      { x: 300, y: 160 },
      { x: 440, y: 130 },
      { x: 560, y: 190 },
    ],
  },
  // The mountains: shorter, harder
  {
    from: 'fork1',
    to: 'merge1',
    cells: 6,
    points: [
      { x: 390, y: 400 },
      { x: 480, y: 390 },
      { x: 545, y: 300 },
      { x: 560, y: 190 },
    ],
  },
  {
    from: 'merge1',
    to: 'fork2',
    cells: 9,
    points: [
      { x: 560, y: 190 },
      { x: 680, y: 150 },
      { x: 800, y: 190 },
      { x: 860, y: 290 },
      { x: 820, y: 390 },
      { x: 760, y: 470 },
    ],
  },
  // The swamp
  {
    from: 'fork2',
    to: 'merge2',
    cells: 7,
    points: [
      { x: 760, y: 470 },
      { x: 700, y: 570 },
      { x: 790, y: 660 },
      { x: 960, y: 650 },
    ],
  },
  // The city
  {
    from: 'fork2',
    to: 'merge2',
    cells: 5,
    points: [
      { x: 760, y: 470 },
      { x: 890, y: 440 },
      { x: 990, y: 520 },
      { x: 960, y: 650 },
    ],
  },
  {
    from: 'merge2',
    to: 'finish',
    cells: 10,
    points: [
      { x: 960, y: 650 },
      { x: 1060, y: 710 },
      { x: 1130, y: 620 },
      { x: 1110, y: 510 },
      { x: 1140, y: 400 },
      { x: 1100, y: 290 },
      { x: 1110, y: 170 },
      { x: 1080, y: 90 },
    ],
  },
];

const zoneOutlines: { theme: ZoneTheme; outline: Point[] }[] = [
  {
    theme: 'meadow',
    outline: [
      { x: 20, y: 780 },
      { x: 20, y: 560 },
      { x: 180, y: 500 },
      { x: 300, y: 430 },
      { x: 440, y: 470 },
      { x: 460, y: 620 },
      { x: 380, y: 780 },
    ],
  },
  {
    theme: 'forest',
    outline: [
      { x: 20, y: 520 },
      { x: 20, y: 90 },
      { x: 200, y: 40 },
      { x: 460, y: 60 },
      { x: 520, y: 150 },
      { x: 420, y: 250 },
      { x: 350, y: 390 },
      { x: 170, y: 470 },
    ],
  },
  {
    theme: 'mountains',
    outline: [
      { x: 420, y: 330 },
      { x: 520, y: 170 },
      { x: 640, y: 110 },
      { x: 760, y: 150 },
      { x: 700, y: 380 },
      { x: 560, y: 480 },
      { x: 440, y: 440 },
    ],
  },
  {
    theme: 'swamp',
    outline: [
      { x: 440, y: 780 },
      { x: 500, y: 560 },
      { x: 640, y: 470 },
      { x: 790, y: 520 },
      { x: 900, y: 620 },
      { x: 880, y: 780 },
    ],
  },
  {
    theme: 'city',
    outline: [
      { x: 730, y: 420 },
      { x: 780, y: 120 },
      { x: 960, y: 90 },
      { x: 1040, y: 260 },
      { x: 1060, y: 480 },
      { x: 980, y: 590 },
      { x: 830, y: 520 },
    ],
  },
  {
    theme: 'castle',
    outline: [
      { x: 900, y: 780 },
      { x: 920, y: 620 },
      { x: 1060, y: 520 },
      { x: 1060, y: 30 },
      { x: 1180, y: 30 },
      { x: 1180, y: 780 },
    ],
  },
];

function build(): Board {
  const cells: BoardCell[] = [];
  const edges: BoardEdge[] = [];
  const roads: Point[][] = [];
  const junctions = new Map<string, number>();
  const add = (p: Point, kind: CellKind) => {
    const id = cells.length + 1;
    cells.push({ x: p.x, y: p.y, id, kind });
    return id;
  };
  for (const segment of segments) {
    const road = catmullRom(segment.points, 16);
    roads.push(road);
    const spots = evenly(road, segment.cells);
    let previous =
      junctions.get(segment.from) ??
      add(spots[0] as Point, segment.from === 'start' ? 'start' : 'fork');
    junctions.set(segment.from, previous);
    for (let k = 1; k < spots.length; k++) {
      const last = k === spots.length - 1;
      let id: number;
      if (last && junctions.has(segment.to)) {
        id = junctions.get(segment.to) as number;
      } else {
        const kind: CellKind = last
          ? segment.to === 'finish'
            ? 'finish'
            : 'fork'
          : k % 4 === 2
            ? 'event'
            : 'plain';
        id = add(spots[k] as Point, kind);
        if (last) junctions.set(segment.to, id);
      }
      edges.push({ from: previous, to: id });
      previous = id;
    }
  }
  // Checkpoints: the merges, where both ways meet again
  for (const name of ['merge1', 'merge2']) {
    const cell = cells[(junctions.get(name) as number) - 1];
    if (cell) cell.kind = 'checkpoint';
  }
  return {
    width: 1200,
    height: 800,
    cells,
    edges,
    roads,
    zones: zoneOutlines.map((z) => ({
      id: z.theme,
      theme: z.theme,
      name: ru.styleguide.zones[z.theme],
      outline: z.outline,
    })),
  };
}

export const demoBoard: Board = build();
