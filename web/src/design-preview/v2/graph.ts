// The preview map (G2, second round): a free graph like stage 2's — two forks that meet again — laid over a world of
// zones. Segments are drawn through control points; cells stand evenly along each one. The same graph serves both map
// variants and the movement prototype. Coordinates are in a 1200×800 world.

export type Point = { x: number; y: number };

export type ZoneId = 'meadow' | 'forest' | 'mountains' | 'swamp' | 'city' | 'castle';

export type CellKind = 'start' | 'finish' | 'plain' | 'event' | 'checkpoint' | 'fork';

export type Cell = Point & { id: number; kind: CellKind; zone: ZoneId };

export type Edge = { from: number; to: number };

export const world = { width: 1200, height: 800 };

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

// Zones as closed regions, drawn smooth through these points
export const zones: { id: ZoneId; points: Point[]; label: Point }[] = [
  {
    id: 'meadow',
    label: { x: 150, y: 610 },
    points: [
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
    id: 'forest',
    label: { x: 200, y: 180 },
    points: [
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
    id: 'mountains',
    label: { x: 560, y: 330 },
    points: [
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
    id: 'swamp',
    label: { x: 640, y: 690 },
    points: [
      { x: 440, y: 780 },
      { x: 500, y: 560 },
      { x: 640, y: 470 },
      { x: 790, y: 520 },
      { x: 900, y: 620 },
      { x: 880, y: 780 },
    ],
  },
  {
    id: 'city',
    label: { x: 920, y: 380 },
    points: [
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
    id: 'castle',
    label: { x: 1085, y: 760 },
    points: [
      { x: 900, y: 780 },
      { x: 920, y: 620 },
      { x: 1060, y: 520 },
      { x: 1060, y: 30 },
      { x: 1180, y: 30 },
      { x: 1180, y: 780 },
    ],
  },
];

function catmullRom(points: Point[], samples: number): Point[] {
  const out: Point[] = [];
  for (let i = 0; i < points.length - 1; i++) {
    const p0 = points[Math.max(i - 1, 0)] as Point;
    const p1 = points[i] as Point;
    const p2 = points[i + 1] as Point;
    const p3 = points[Math.min(i + 2, points.length - 1)] as Point;
    for (let s = 0; s < samples; s++) {
      const t = s / samples;
      const t2 = t * t;
      const t3 = t2 * t;
      out.push({
        x:
          0.5 *
          (2 * p1.x +
            (-p0.x + p2.x) * t +
            (2 * p0.x - 5 * p1.x + 4 * p2.x - p3.x) * t2 +
            (-p0.x + 3 * p1.x - 3 * p2.x + p3.x) * t3),
        y:
          0.5 *
          (2 * p1.y +
            (-p0.y + p2.y) * t +
            (2 * p0.y - 5 * p1.y + 4 * p2.y - p3.y) * t2 +
            (-p0.y + 3 * p1.y - 3 * p2.y + p3.y) * t3),
      });
    }
  }
  out.push(points[points.length - 1] as Point);
  return out;
}

/** Points evenly spaced along a curve: count + 1 of them, the ends included. */
function evenly(curve: Point[], count: number): Point[] {
  const lengths = [0];
  for (let i = 1; i < curve.length; i++) {
    const a = curve[i - 1] as Point;
    const b = curve[i] as Point;
    lengths.push((lengths[i - 1] as number) + Math.hypot(b.x - a.x, b.y - a.y));
  }
  const total = lengths[lengths.length - 1] as number;
  const out: Point[] = [];
  let j = 0;
  for (let k = 0; k <= count; k++) {
    const target = (total * k) / count;
    while (j < lengths.length - 2 && (lengths[j + 1] as number) < target) j++;
    const a = curve[j] as Point;
    const b = curve[j + 1] as Point;
    const span = (lengths[j + 1] as number) - (lengths[j] as number) || 1;
    const t = (target - (lengths[j] as number)) / span;
    out.push({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
  }
  return out;
}

/** Closed smooth path through the points: the zones' outlines. */
export function blob(points: Point[]): string {
  const ring = [...points, points[0] as Point, points[1] as Point, points[2] as Point];
  const curve = catmullRom(ring, 12).slice(12, -12 * 1);
  return `M${curve.map((p) => `${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' L')} Z`;
}

function inside(point: Point, polygon: Point[]): boolean {
  let hit = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const a = polygon[i] as Point;
    const b = polygon[j] as Point;
    if (
      a.y > point.y !== b.y > point.y &&
      point.x < ((b.x - a.x) * (point.y - a.y)) / (b.y - a.y) + a.x
    )
      hit = !hit;
  }
  return hit;
}

/** The zone a point lies in, or null in the sea around the island */
export function zoneOf(point: Point): ZoneId | null {
  return zones.find((z) => inside(point, z.points))?.id ?? null;
}

export function zoneAt(point: Point): ZoneId {
  return zoneOf(point) ?? 'meadow';
}

function build() {
  const cells: Cell[] = [];
  const edges: Edge[] = [];
  const junctions = new Map<string, number>();
  const curves: { d: string }[] = [];
  const add = (p: Point, kind: CellKind) => {
    const cell = { ...p, id: cells.length + 1, kind, zone: zoneAt(p) };
    cells.push(cell);
    return cell.id;
  };
  for (const segment of segments) {
    const curve = catmullRom(segment.points, 16);
    curves.push({ d: `M${curve.map((p) => `${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' L')}` });
    const spots = evenly(curve, segment.cells);
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
  return { cells, edges, curves, junctions };
}

export const graph = build();

export function cell(id: number): Cell {
  return graph.cells[id - 1] as Cell;
}

/** A path from one cell forward by steps, taking at each fork the way given (or the first). */
export function walk(from: number, steps: number, prefer: 'first' | 'second' = 'second'): number[] {
  const path = [from];
  let at = from;
  for (let s = 0; s < steps; s++) {
    const next = graph.edges.filter((e) => e.from === at);
    if (next.length === 0) break;
    const edge = next.length > 1 && prefer === 'second' ? next[1] : next[0];
    at = (edge as Edge).to;
    path.push(at);
  }
  return path;
}
