import { ru } from '../i18n/ru';
import type { components } from '../api/schema';
import { catmullRom } from './geometry';
import type { Board, BoardCell, BoardEdge, BoardZone, CellKind, Point, ZoneTheme } from './types';

// Stage 2's map is the season's graph as the editor drew it (D-300): cells with their places, arrows, zones. The board
// takes the places as they are (framed with a margin), numbers the cells from the start along the arrows, joins the
// arrows into roads between junctions and draws each zone as a blob around its cells. A map without places (made
// outside the editor) is laid out in columns by the distance from the start.

export type MapCell = components['schemas']['CellView'];
export type MapEdge = components['schemas']['EdgeView'];
export type MapZone = components['schemas']['ZoneView'];
export type GraphMap = { cells: MapCell[]; edges: MapEdge[]; zones: MapZone[] };

/** The world's margin round the outermost cells, and the gap of the automatic layout */
const margin = 90;
const column = 110;
const row = 100;
/** How far a zone's blob reaches past its cells */
const zoneReach = 58;

/** A zone's look by its place in the map's list: the meadow is left for the ground round the start */
export const zoneThemes: ZoneTheme[] = ['forest', 'mountains', 'swamp', 'city', 'castle', 'meadow'];

export function zoneTheme(index: number): ZoneTheme {
  return zoneThemes[index % zoneThemes.length] as ZoneTheme;
}

/** A zone's colour on the map, and on its swatch in the map's legend */
export const zoneFill: Record<ZoneTheme, string> = {
  meadow: 'var(--color-zone-meadow)',
  forest: 'var(--color-zone-forest)',
  mountains: 'var(--color-zone-mountains)',
  swamp: 'var(--color-zone-swamp)',
  city: 'var(--color-zone-city)',
  castle: 'var(--color-zone-castle)',
};

const kinds: Record<MapCell['type'], CellKind> = {
  start: 'start',
  empty: 'plain',
  finish: 'finish',
  fork: 'fork',
  teleport: 'teleport',
  checkpoint: 'checkpoint',
  pointsBonus: 'bonus',
  event: 'event',
  shop: 'shop',
};

/** The arrows out of a cell in the order of a branch choice: the default branch first, then by the cell's id */
export function exitsOf(edges: MapEdge[], id: string): MapEdge[] {
  return edges
    .filter((e) => e.from === id)
    .sort(
      (a, b) =>
        Number(b.isDefaultForward) - Number(a.isDefaultForward) ||
        (a.to < b.to ? -1 : a.to > b.to ? 1 : 0),
    );
}

/** The cells in the order of a walk from the start: breadth first along the arrows (default first), then teleports */
function numbering(map: GraphMap): string[] {
  const start = map.cells.find((c) => c.type === 'start');
  const order: string[] = [];
  const seen = new Set<string>();
  const queue = start ? [start.id] : [];
  if (start) seen.add(start.id);
  const byId = new Map(map.cells.map((c) => [c.id, c]));
  while (queue.length > 0) {
    const id = queue.shift() as string;
    order.push(id);
    const next = exitsOf(map.edges, id).map((e) => e.to);
    const to = byId.get(id)?.to;
    if (to) next.push(to);
    for (const n of next)
      if (!seen.has(n) && byId.has(n)) {
        seen.add(n);
        queue.push(n);
      }
  }
  // A cell no way reaches (a draft in the editor) still gets a number, after the others
  for (const c of map.cells) if (!seen.has(c.id)) order.push(c.id);
  return order;
}

/** Places for a map without them: a column per step from the start, the cells of a column one under another */
function layout(order: string[], map: GraphMap): Map<string, Point> {
  const depth = new Map<string, number>();
  const start = order[0];
  if (start !== undefined) depth.set(start, 0);
  for (const id of order) {
    const d = depth.get(id) ?? 0;
    for (const e of map.edges)
      if (e.from === id && !depth.has(e.to)) {
        depth.set(e.to, d + 1);
      }
  }
  const rows = new Map<number, number>();
  const places = new Map<string, Point>();
  for (const id of order) {
    const d = depth.get(id) ?? 0;
    const r = rows.get(d) ?? 0;
    rows.set(d, r + 1);
    places.set(id, { x: d * column, y: r * row });
  }
  return places;
}

/** Points round each cell of a zone, wrapped in a convex hull: the zone's outline */
function hull(points: Point[]): Point[] {
  const sorted = [...points].sort((a, b) => a.x - b.x || a.y - b.y);
  if (sorted.length < 3) return sorted;
  const cross = (o: Point, a: Point, b: Point) =>
    (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
  const lower: Point[] = [];
  for (const p of sorted) {
    while (
      lower.length >= 2 &&
      cross(lower[lower.length - 2] as Point, lower[lower.length - 1] as Point, p) <= 0
    )
      lower.pop();
    lower.push(p);
  }
  const upper: Point[] = [];
  for (const p of [...sorted].reverse()) {
    while (
      upper.length >= 2 &&
      cross(upper[upper.length - 2] as Point, upper[upper.length - 1] as Point, p) <= 0
    )
      upper.pop();
    upper.push(p);
  }
  return [...lower.slice(0, -1), ...upper.slice(0, -1)];
}

function outline(cells: Point[]): Point[] {
  const around = cells.flatMap((c) =>
    Array.from({ length: 8 }, (_, k) => ({
      x: c.x + Math.cos((k * Math.PI) / 4) * zoneReach,
      y: c.y + Math.sin((k * Math.PI) / 4) * zoneReach,
    })),
  );
  return hull(around);
}

/** The roads: runs of arrows between junctions (a cell with other than one way in and one way out), smoothed */
function roads(cells: BoardCell[], edges: BoardEdge[]): Point[][] {
  const at = new Map(cells.map((c) => [c.id, c]));
  const outs = new Map<number, number[]>();
  const ins = new Map<number, number>();
  for (const e of edges) {
    outs.set(e.from, [...(outs.get(e.from) ?? []), e.to]);
    ins.set(e.to, (ins.get(e.to) ?? 0) + 1);
  }
  const inner = (id: number) => (outs.get(id)?.length ?? 0) === 1 && ins.get(id) === 1;
  const used = new Set<string>();
  const lines: Point[][] = [];
  const follow = (from: number, first: number) => {
    const line = [at.get(from) as Point];
    let previous = from;
    let current = first;
    while (!used.has(`${previous}>${current}`)) {
      used.add(`${previous}>${current}`);
      line.push(at.get(current) as Point);
      if (!inner(current)) break;
      previous = current;
      current = (outs.get(current) as number[])[0] as number;
    }
    lines.push(line.length > 2 ? catmullRom(line, 8) : line);
  };
  for (const c of cells) if (!inner(c.id)) for (const to of outs.get(c.id) ?? []) follow(c.id, to);
  // A ring of inner cells has no junction to start from
  for (const e of edges) if (!used.has(`${e.from}>${e.to}`)) follow(e.from, e.to);
  return lines;
}

/** The board of the season's graph map, and the board's number for each of the server's cell ids */
export function graphBoard(map: GraphMap): { board: Board; cellNumber: Map<string, number> } {
  const order = numbering(map);
  const cellNumber = new Map(order.map((id, i) => [id, i + 1]));
  const byId = new Map(map.cells.map((c) => [c.id, c]));
  const placed = map.cells.length > 0 && map.cells.every((c) => c.x != null && c.y != null);
  const auto = placed ? null : layout(order, map);
  const raw = new Map(
    order.map((id) => {
      const c = byId.get(id) as MapCell;
      return [id, auto ? (auto.get(id) as Point) : { x: Number(c.x), y: Number(c.y) }];
    }),
  );
  const xs = [...raw.values()].map((p) => p.x);
  const ys = [...raw.values()].map((p) => p.y);
  const minX = xs.length > 0 ? Math.min(...xs) : 0;
  const minY = ys.length > 0 ? Math.min(...ys) : 0;
  const width = (xs.length > 0 ? Math.max(...xs) - minX : 0) + margin * 2;
  const height = (ys.length > 0 ? Math.max(...ys) - minY : 0) + margin * 2;

  const cells: BoardCell[] = order.map((id) => {
    const c = byId.get(id) as MapCell;
    const p = raw.get(id) as Point;
    return {
      x: p.x - minX + margin,
      y: p.y - minY + margin,
      id: cellNumber.get(id) as number,
      kind: kinds[c.type],
      to: c.to ? cellNumber.get(c.to) : undefined,
      amount: c.amount ?? undefined,
      zone: c.zone ?? undefined,
    };
  });
  const edges: BoardEdge[] = map.edges
    .filter((e) => cellNumber.has(e.from) && cellNumber.has(e.to))
    .map((e) => ({ from: cellNumber.get(e.from) as number, to: cellNumber.get(e.to) as number }));
  const zones: BoardZone[] = map.zones.flatMap((z, i) => {
    const inside = cells.filter((c) => c.zone === z.id);
    return inside.length === 0
      ? []
      : [{ id: z.id, name: z.name, theme: zoneTheme(i), outline: outline(inside) }];
  });
  return { board: { width, height, cells, edges, roads: roads(cells, edges), zones }, cellNumber };
}

/** Where a branch leads with the steps left (a hint for the choice, the server walks for real, D-304) */
export type BranchEnd =
  | { kind: 'cell'; cell: string }
  | { kind: 'teleport'; cell: string; to: string }
  | { kind: 'fork'; cell: string }
  | { kind: 'finish'; cell: string };

/**
 * Where the player's own move ends if they take the branch into `first` with `steps` left at the fork: along the only
 * exit of each cell, pausing at the next fork with steps left, stopping at the finish; a stop on a teleport transfers.
 */
export function branchEnd(map: GraphMap, first: string, steps: number): BranchEnd {
  const byId = new Map(map.cells.map((c) => [c.id, c]));
  let current = first;
  for (let left = steps - 1; left > 0 && byId.get(current)?.type !== 'finish'; left--) {
    const exits = exitsOf(map.edges, current);
    if (exits.length > 1) return { kind: 'fork', cell: current };
    if (exits.length === 0) break;
    current = (exits[0] as MapEdge).to;
  }
  const cell = byId.get(current);
  if (cell?.type === 'finish') return { kind: 'finish', cell: current };
  if (cell?.type === 'teleport' && cell.to) return { kind: 'teleport', cell: current, to: cell.to };
  return { kind: 'cell', cell: current };
}

type Leg = components['schemas']['MoveLegView'];

/**
 * The server's legs of a move as the token walks them: its old cell first, then every cell entered; a transfer
 * (a teleport, the admin's move) is a flight, listed by the index of the cell it lands on. Null when it entered nothing.
 */
export function legsPath(
  legs: Leg[],
  cellNumber: Map<string, number>,
): { path: number[]; jumps: number[] } | null {
  const first = legs[0];
  if (!first) return null;
  const path: number[] = [];
  const jumps: number[] = [];
  const number = (id: string) => cellNumber.get(id);
  const start = number(first.from);
  if (start === undefined) return null;
  path.push(start);
  for (const leg of legs) {
    const transfer = leg.reason === 'teleport' || leg.reason === 'adminAdjustment';
    for (const [k, id] of leg.path.entries()) {
      const n = number(id);
      if (n === undefined) return null;
      if (transfer && k === 0) jumps.push(path.length);
      path.push(n);
    }
  }
  return path.length > 1 ? { path, jumps } : null;
}

/** A zone's rules in words (CONTENT.md «Зона», D-307): the roll filter, the dice, the drop penalty */
export function zoneRules(zone: MapZone): string[] {
  const t = ru.map;
  const rules: string[] = [];
  const f = zone.rollFilter;
  if (f) {
    const parts = [
      ...(f.tags && f.tags.length > 0 ? [t.zones.tags(f.tags)] : []),
      ...(f.minHours != null || f.maxHours != null
        ? [t.zones.hours(f.minHours ?? null, f.maxHours ?? null)]
        : []),
      ...(f.releaseYearBefore != null ? [t.zones.yearBefore(f.releaseYearBefore)] : []),
    ];
    if (parts.length > 0) rules.push(t.zones.filter(parts.join(', ')));
  }
  const dice = zone.diceModifier;
  if (dice && dice.value !== 0) {
    if (dice.stage === 'count') rules.push(t.zones.diceCount(dice.value));
    else if (dice.stage === 'add') rules.push(t.zones.diceAdd(dice.value));
  }
  if (zone.dropPenaltyMultiplier != null && zone.dropPenaltyMultiplier !== 1)
    rules.push(t.zones.drop(zone.dropPenaltyMultiplier));
  return rules.length > 0 ? rules : [t.zones.plain];
}

export type BranchOption = {
  /** The server's cell the branch leads to: what the choice sends back */
  id: string;
  /** The board's number of that cell, as the map marks it */
  cell: number;
  /** Where the steps left would take the token, in words */
  end: string;
  /** The zone of the cell where the token will stand (past a teleport, its destination): it decides the next roll */
  zone: string | null;
  /** That zone's rules in words (D-307) */
  rules: string[];
};

/** The options of a branch choice as the page shows them: numbered as on the map, with where each leads */
export function branchOptions(
  choice: components['schemas']['ChoiceView'],
  map: GraphMap,
  cellNumber: Map<string, number>,
): BranchOption[] {
  const steps = choice.steps ?? 1;
  const t = ru.map.branch;
  const number = (id: string) => cellNumber.get(id) ?? 0;
  const zoneOf = (id: string) => {
    const zone = map.cells.find((c) => c.id === id)?.zone;
    return map.zones.find((z) => z.id === zone) ?? null;
  };
  return choice.options.map((o) => {
    const end = branchEnd(map, o.id, steps);
    const zone = zoneOf(end.kind === 'teleport' ? end.to : end.cell);
    return {
      id: o.id,
      cell: number(o.id),
      zone: zone?.name ?? null,
      rules: zone ? zoneRules(zone) : [],
      end:
        end.kind === 'finish'
          ? t.endFinish
          : end.kind === 'fork'
            ? t.endFork(number(end.cell))
            : end.kind === 'teleport'
              ? t.endTeleport(number(end.cell), number(end.to))
              : t.endCell(number(end.cell)),
    };
  });
}
