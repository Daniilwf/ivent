import type { Board, Point } from './types';

// The map's camera and the places of the stickers on a cell: pure geometry, apart from MapView for its tests (K2-2…K2-4)

/** The view is its centre and its width in world units; the height follows the frame's shape */
export type View = { cx: number; cy: number; w: number };
export type Box = { width: number; height: number };

/**
 * The map's camera scales (frame pixels per world unit) and margins (K2-2, K2-3, D-641): a wide frame shows the whole
 * board only while its cells stay readable; a bigger board opens around me, about as close as the phone does. The
 * view may pass the board's edge by a sticker's fan, so the players on an edge cell (the start) are never cut. The
 * whole board keeps clear of the zoom buttons' band at the bottom of the frame (`tools`, frame pixels, K2-4).
 */
export const camera = { readable: 0.5, closeUp: 0.8, edge: 64, tools: 64 };

/** The first view of a frame: the whole board, or around me (`focus`: my cell off the start; `anchor`: my cell) */
export function openingView(
  board: Board,
  box: Box,
  focus: Point | null,
  anchor: Point | null,
): View {
  const ratio = box.height / box.width;
  // Around a point, but never past the board's edge (and its margin) while the board is bigger than the view
  const clampTo = (at: number, half: number, size: number) =>
    half * 2 >= size + 2 * camera.edge
      ? size / 2
      : Math.min(Math.max(at, half - camera.edge), size - half + camera.edge);
  // A tall frame (a phone) fills its height and centres on me; a wide one fits the whole board. Both fit the board
  // into the frame above the zoom buttons' band and move it up by half the band, so no button covers a cell.
  const free = Math.max(box.height - camera.tools, box.height / 2) / box.width;
  const portrait = free > board.height / board.width && box.width < 600;
  const fitWidth = portrait
    ? (board.height / free) * 1.04
    : Math.max(board.width, board.height / free) * 1.04;
  const fit: View = {
    cx: portrait
      ? clampTo(anchor?.x ?? board.width / 2, fitWidth / 2, board.width)
      : board.width / 2,
    cy: board.height / 2 + ((box.height - free * box.width) / 2) * (fitWidth / box.width),
    w: fitWidth,
  };
  const around = (at: Point, w: number): View => ({
    w,
    cx: clampTo(at.x, w / 2, board.width),
    cy: clampTo(at.y, (w * ratio) / 2, board.height),
  });
  if (box.width < 600)
    return focus ? around(focus, Math.min(Math.max(box.width, 320), fit.w)) : fit;
  // A desktop: the whole board while it is readable, otherwise a close look around me (or the start)
  const near = focus ?? anchor;
  return box.width / fit.w >= camera.readable || !near
    ? fit
    : around(near, box.width / camera.closeUp);
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
