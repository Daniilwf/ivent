// The board as the interface draws it (G3): a free graph of cells laid over a world of zones, and the players on it.
// Stage 1's straight map and stage 2's forks are both such a graph (docs/SPEC.md, CLAUDE.md invariant 10).

export type Point = { x: number; y: number };

/** How a zone looks; which zones a season has and their names come from the content */
export type ZoneTheme = 'meadow' | 'forest' | 'mountains' | 'swamp' | 'city' | 'castle';

export type CellKind = 'start' | 'finish' | 'plain' | 'event' | 'checkpoint' | 'fork';

export type BoardCell = Point & { id: number; kind: CellKind };

export type BoardEdge = { from: number; to: number };

export type BoardZone = { id: string; name: string; theme: ZoneTheme; outline: Point[] };

export type Board = {
  width: number;
  height: number;
  cells: BoardCell[];
  edges: BoardEdge[];
  /** The road drawn under the cells: one polyline per stretch between forks */
  roads: Point[][];
  zones: BoardZone[];
};

export type Player = {
  id: string;
  name: string;
  /** The player's number in the season: picks the token colour */
  token: number;
  /** A small avatar (a GIF thumbnail); without it, the first letter on the token colour */
  avatar?: string | undefined;
  cell: number;
  points: number;
  me?: boolean;
  first?: boolean;
  inactive?: boolean;
};
