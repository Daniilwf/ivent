import type { Board, Point } from './types';
import { moveMotion } from '../design/motion';

// The map's camera and the places of the stickers on a cell: pure geometry, apart from MapView for its tests
// (K1M-2, K1M-6, K2-2…K2-4)

/** The view is its centre and its width in world units; the height follows the frame's shape */
export type View = { cx: number; cy: number; w: number };
export type Box = { width: number; height: number };
export type Bounds = { x0: number; x1: number; y0: number; y1: number };
/** The side of the frame the zoom buttons stand on (the bottom on every size, K2-4), or none */
export type ToolsSide = 'bottom' | null;

/**
 * The map's labels (DESIGN.md «Карта»): the smallest one on the board is drawn at `world` units (text-xs, «Старт» and
 * «Финиш»), and none is shown smaller than `min` frame pixels, the same as `--map-label-min` in tokens.css, which a
 * test keeps equal. The camera's readable scale follows from them.
 */
export const mapLabel = { min: 9, world: 12 };

/**
 * The map's camera scales (frame pixels per world unit) and margins (K2-2, K2-3, D-641): a wide frame shows the whole
 * board only while its labels stay readable (`mapLabel`); a bigger board opens closer (`closeUp`, readable too). The
 * view may pass the board's edge by a sticker's fan (`edge`, world units), so the players on an edge cell (the start)
 * and my «ты» are never cut (K1M-6). The whole board keeps clear of the zoom buttons' band (`tools`, frame pixels:
 * 44 px buttons 12 px from the frame's edge and a little air, K1M-6, K2-4). A pointer that moved less than `tapSlop`
 * frame pixels between down and up tapped (K1M-7).
 */
export const camera = {
  readable: mapLabel.min / mapLabel.world,
  closeUp: 0.9,
  edge: 64,
  tools: 64,
  tapSlop: 8,
};

export const stickerLift = { onCell: 32, onBigCell: 52 };

/**
 * The world a frame's camera may show (K1M-2): the board with room round its edge and, on the buttons' side, the band
 * they stand on (in world units at the view's zoom); `clamp` moves a view back into it, `fit` shows all of it.
 */
export function cameraFrame(
  board: Board,
  box: Box,
  side: ToolsSide,
  anchor: Point | null = null,
  extra?: Bounds,
) {
  const ratio = box.height / box.width;
  const gap = side ? camera.tools : 0;
  const area = (w: number) => {
    const strip = (gap * w) / box.width;
    const world = {
      x0: -camera.edge,
      x1: board.width + camera.edge,
      y0: -camera.edge,
      y1: board.height + camera.edge + strip,
    };
    return extra ? union(world, extra) : world;
  };
  /** A view moved back into the world: never past its edges, and centred on it once it is all in the frame */
  const clamp = (v: View): View => {
    const a = area(v.w);
    const h = v.w * ratio;
    const along = (at: number, half: number, low: number, high: number) =>
      half * 2 >= high - low ? (low + high) / 2 : Math.min(Math.max(at, low + half), high - half);
    return {
      w: v.w,
      cx: along(v.cx, v.w / 2, a.x0, a.x1),
      cy: along(v.cy, h / 2, a.y0, a.y1),
    };
  };
  // The width at which the whole board's height, its room and the buttons' band fill the frame's height
  const tallWidth =
    ((board.height + camera.edge * 2) * box.width) / Math.max(box.height - gap, box.height / 2);
  // Only a phone's frame crops the board around me; a desktop's shows the whole board while it is readable
  const portrait = ratio > board.height / board.width && box.width < 600;
  // A wide frame shows the whole board; a tall one (a phone) fills its height and centres on me
  const wholeWidth = Math.max(board.width + camera.edge * 2, tallWidth);
  const fit = clamp({
    cx: portrait ? (anchor?.x ?? board.width / 2) : board.width / 2,
    cy: board.height / 2,
    w: portrait ? tallWidth : wholeWidth,
  });
  return { ratio, area, clamp, wholeWidth, fit };
}

const union = (a: Bounds, b: Bounds): Bounds => ({
  x0: Math.min(a.x0, b.x0),
  x1: Math.max(a.x1, b.x1),
  y0: Math.min(a.y0, b.y0),
  y1: Math.max(a.y1, b.y1),
});

export type MoveHop = { start: number; finish: number; height: number; duration: number };

export function moveHops(points: readonly Point[], jumps: readonly number[], budget: number) {
  const steps = points.length - 1;
  if (steps < 1) return [];
  const units = steps + jumps.filter((i) => i > 0 && i <= steps).length;
  const long = units * moveMotion.minHopSeconds > budget;
  const groups = long
    ? Math.min(steps, Math.max(1, Math.floor(budget / moveMotion.minHopSeconds)))
    : steps;
  const hop = Math.min(moveMotion.hopSeconds, budget / Math.max(1, long ? groups : units));

  return Array.from({ length: groups }, (_, group): MoveHop => {
    // A long route groups several cells into a single hop, still crossing them in order.
    const start = Math.round((group * steps) / groups);
    const finish = Math.round(((group + 1) * steps) / groups);
    const from = points[start] as Point;
    const to = points[finish] as Point;
    const flight = finish === start + 1 && jumps.includes(finish);
    const height = flight
      ? Math.max(Math.hypot(to.x - from.x, to.y - from.y) / 3, 40)
      : moveMotion.hopHeight;
    return { start, finish, height, duration: flight && !long ? hop * 2 : hop };
  });
}

export type MoveFraming = {
  hops: readonly MoveHop[];
  easeInOut: (time: number) => number;
  /** Bounds of the sticker relative to the ground point before adding the hop. */
  sticker: Bounds;
};

export function hopPoint(path: readonly Point[], hop: MoveHop, progress: number): Point {
  const at = hop.start + (hop.finish - hop.start) * progress;
  const index = Math.min(hop.finish - 1, Math.floor(at));
  const a = path[index] as Point;
  const b = path[index + 1] as Point;
  const along = at - index;
  return { x: a.x + (b.x - a.x) * along, y: a.y + (b.y - a.y) * along };
}

// Divide each hop into timed envelopes so the camera can follow even a fast hop with a large arc.
const hopSpans = 4;

/** An initial shot, with a fixed-zoom sweep when the route would otherwise need a wider shot. */
export function moveView(view: View, box: Box, path: readonly Point[], framing: MoveFraming) {
  const from = path[0];
  const to = path.at(-1);
  if (!from || !to) return;
  const { easeInOut, hops, sticker } = framing;
  const ratio = box.height / box.width;
  const footprint = (p: Point): Bounds => ({
    x0: p.x + sticker.x0,
    x1: p.x + sticker.x1,
    y0: p.y + sticker.y0,
    y1: p.y + sticker.y1,
  });
  const start = footprint(from);
  const spans = hops.flatMap((hop) =>
    Array.from({ length: hopSpans }, (_, i) => {
      const a = easeInOut(i / hopSpans);
      const b = easeInOut((i + 1) / hopSpans);
      const steps = hop.finish - hop.start;
      const inside = path.slice(
        hop.start + Math.ceil(steps * a),
        hop.start + Math.floor(steps * b) + 1,
      );
      const area = [hopPoint(path, hop, a), ...inside, hopPoint(path, hop, b)]
        .map(footprint)
        .reduce(union);
      const peak = Math.sin(Math.PI * Math.min(b, Math.max(a, 0.5))) * hop.height;
      return { duration: hop.duration / hopSpans, area: { ...area, y0: area.y0 - peak } };
    }),
  );
  const bounds = spans.reduce((area, span) => union(area, span.area), start);
  if (
    bounds.x0 >= view.cx - view.w / 2 &&
    bounds.x1 <= view.cx + view.w / 2 &&
    bounds.y0 >= view.cy - (view.w * ratio) / 2 &&
    bounds.y1 <= view.cy + (view.w * ratio) / 2
  )
    return;

  const shot = ({ x0, x1, y0, y1 }: Bounds, minWidth: number): View => ({
    cx: (x0 + x1) / 2,
    cy: (y0 + y1) / 2,
    w: Math.max(minWidth, x1 - x0, (y1 - y0) / ratio),
  });
  const still = shot(bounds, view.w);
  const readable = Math.max(view.w, box.width / camera.readable);
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const distance = Math.hypot(dx, dy);
  const duration = hops.reduce((total, hop) => total + hop.duration, 0);
  if (still.w <= readable || distance === 0 || duration === 0) return { bounds, target: still };

  const speed = Math.min(
    readable * Math.min(1, ratio) * moveMotion.cameraScreensPerSecond,
    distance / duration,
  );
  const vx = (speed * dx) / distance;
  const vy = (speed * dy) / distance;
  let elapsed = 0;
  // Frame the hop envelopes in coordinates moving with the camera. Reserving each envelope
  // throughout its hop keeps fast jumps visible even when they can outrun the camera.
  const swept = spans.reduce((required, { area, duration }) => {
    const end = elapsed + duration;
    const shift = (at: number) => ({
      x0: area.x0 - vx * at,
      x1: area.x1 - vx * at,
      y0: area.y0 - vy * at,
      y1: area.y1 - vy * at,
    });
    const covered = union(required, union(shift(elapsed), shift(end)));
    elapsed = end;
    return covered;
  }, start);
  const target = shot(swept, readable);
  if (target.w >= still.w) return { bounds, target: still };
  return {
    bounds,
    target,
    follow: (seconds: number): View => {
      const time = Math.min(duration, Math.max(0, seconds));
      return { ...target, cx: target.cx + vx * time, cy: target.cy + vy * time };
    },
  };
}

/**
 * The first view of a frame: the whole board, or around me (`focus`: my cell off the start; `anchor`: my cell, where a
 * tall frame centres). A phone opens around the focus, about one world unit to a pixel of the frame; a desktop page
 * (`desk`, the page's breakpoint) shows the whole board while it is readable, otherwise a close look around me (K2-3).
 * A frame with no token of mine (a spectator, the admin) looks close around the start while the whole board would be
 * unreadable; the zoom-out button still shows all of it. A wide frame on a phone held sideways stays whole, so a
 * vertical swipe there scrolls the page (K1M-2).
 */
export function openingView(
  board: Board,
  box: Box,
  focus: Point | null,
  anchor: Point | null,
  side: ToolsSide = 'bottom',
  desk = true,
): View {
  const { clamp, fit } = cameraFrame(board, box, side, anchor);
  const readable = box.width / fit.w >= camera.readable;
  const start = board.cells.find((c) => c.kind === 'start') ?? null;
  if (box.width < 600) {
    const near = focus ?? (readable ? null : (anchor ?? start));
    return near
      ? clamp({ w: Math.min(Math.max(box.width, 320), fit.w), cx: near.x, cy: near.y })
      : fit;
  }
  const near = focus ?? anchor ?? start;
  return !desk || readable || !near
    ? fit
    : clamp({ w: box.width / camera.closeUp, cx: near.x, cy: near.y });
}

/** Where the sticker number `index` of `count` on a cell stands: several players fan out above it, at most three */
export function fanSpot(at: Point, index: number, count: number, lift: number): Point {
  const angle = count === 1 ? -90 : -150 + (120 / (Math.min(count, 3) - 1)) * index;
  const reach = count === 1 ? lift : lift + 14;
  return {
    x: at.x + Math.cos((angle * Math.PI) / 180) * reach,
    y: at.y + Math.sin((angle * Math.PI) / 180) * reach,
  };
}
