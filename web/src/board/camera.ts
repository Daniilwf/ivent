import type { Board, Point } from './types';

// The map's camera and the places of the stickers on a cell: pure geometry, apart from MapView for its tests
// (K1M-2, K1M-6, K2-2…K2-4)

/** The view is its centre and its width in world units; the height follows the frame's shape */
export type View = { cx: number; cy: number; w: number };
export type Box = { width: number; height: number };
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

/**
 * The world a frame's camera may show (K1M-2): the board with room round its edge and, on the buttons' side, the band
 * they stand on (in world units at the view's zoom); `clamp` moves a view back into it, `fit` shows all of it.
 */
export function cameraFrame(board: Board, box: Box, side: ToolsSide, anchor: Point | null = null) {
  const ratio = box.height / box.width;
  const gap = side ? camera.tools : 0;
  const area = (w: number) => {
    const strip = (gap * w) / box.width;
    return {
      x0: -camera.edge,
      x1: board.width + camera.edge,
      y0: -camera.edge,
      y1: board.height + camera.edge + (side ? strip : 0),
    };
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
