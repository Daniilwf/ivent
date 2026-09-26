import type { Board, BoardCell, BoardEdge, BoardZone, Point } from './types';

/** A smooth curve through the points (Catmull–Rom), as a polyline of `samples` points per span */
export function catmullRom(points: Point[], samples: number): Point[] {
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
      const at = (a: number, b: number, c: number, d: number) =>
        0.5 *
        (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3);
      out.push({ x: at(p0.x, p1.x, p2.x, p3.x), y: at(p0.y, p1.y, p2.y, p3.y) });
    }
  }
  out.push(points[points.length - 1] as Point);
  return out;
}

/** `count + 1` points evenly spaced along a polyline, both ends included */
export function evenly(line: Point[], count: number): Point[] {
  const lengths = [0];
  for (let i = 1; i < line.length; i++) {
    const a = line[i - 1] as Point;
    const b = line[i] as Point;
    lengths.push((lengths[i - 1] as number) + Math.hypot(b.x - a.x, b.y - a.y));
  }
  const total = lengths[lengths.length - 1] as number;
  const out: Point[] = [];
  let j = 0;
  for (let k = 0; k <= count; k++) {
    const target = (total * k) / count;
    while (j < lengths.length - 2 && (lengths[j + 1] as number) < target) j++;
    const a = line[j] as Point;
    const b = line[j + 1] as Point;
    const span = (lengths[j + 1] as number) - (lengths[j] as number) || 1;
    const t = (target - (lengths[j] as number)) / span;
    out.push({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
  }
  return out;
}

export function polylinePath(line: Point[]) {
  return `M${line.map((p) => `${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' L')}`;
}

/** A closed smooth outline through the points: a zone */
export function blobPath(points: Point[]) {
  const ring = [...points, points[0] as Point, points[1] as Point, points[2] as Point];
  const curve = catmullRom(ring, 12).slice(12, -12);
  return `${polylinePath(curve)} Z`;
}

function inside(point: Point, polygon: Point[]) {
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

/** The zone a point lies in, or null outside every zone */
export function zoneAt(zones: BoardZone[], point: Point): BoardZone | null {
  return zones.find((z) => inside(point, z.outline)) ?? null;
}

export function cellById(board: Board, id: number): BoardCell {
  const found = board.cells.find((c) => c.id === id);
  if (!found) throw new Error(`No cell ${id} on the board`);
  return found;
}

/** A path forward by `steps` cells; at a fork it takes the way given (the first or the second edge) */
export function walk(
  board: Board,
  from: number,
  steps: number,
  prefer: 'first' | 'second' = 'first',
): number[] {
  const path = [from];
  let at = from;
  for (let s = 0; s < steps; s++) {
    const next = board.edges.filter((e) => e.from === at);
    if (next.length === 0) break;
    const edge = (next.length > 1 && prefer === 'second' ? next[1] : next[0]) as BoardEdge;
    at = edge.to;
    path.push(at);
  }
  return path;
}

/** Cells left to the finish along the shortest way (breadth-first over the edges) */
export function cellsToFinish(board: Board, from: number): number {
  const finish = board.cells.find((c) => c.kind === 'finish');
  if (!finish) return 0;
  const seen = new Map<number, number>([[from, 0]]);
  const queue = [from];
  while (queue.length > 0) {
    const at = queue.shift() as number;
    const d = seen.get(at) as number;
    if (at === finish.id) return d;
    for (const e of board.edges)
      if (e.from === at && !seen.has(e.to)) {
        seen.set(e.to, d + 1);
        queue.push(e.to);
      }
  }
  return 0;
}
