import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { linearBoard } from './linearBoard';
import { camera, openingView } from './camera';
import { MapView } from './MapView';
import type { Board, Player } from './types';

// The map's frame after the design review of stages 1–2 (K2-2…K2-4, D-641)

const chain = Array.from({ length: 30 }, (_, i) => ({
  id: `c${String(i)}`,
  type: i === 0 ? ('start' as const) : i === 29 ? ('finish' as const) : ('empty' as const),
}));
const { board } = linearBoard(chain);

const crowd = (count: number): Player[] =>
  Array.from({ length: count }, (_, i) => ({
    id: `p${String(i)}`,
    name: `Игрок ${String(i)}`,
    token: i + 1,
    cell: 1,
    points: 0,
    me: i === 0,
  }));

/** A big world, as a season's map over a picture is (the demo map is 2070 × 1520) */
const big: Board = { ...board, width: 2070, height: 1520 };

describe('MapView', () => {
  it('puts «+N» in the stickers’ fan above a crowded start, not over its label (K2-2)', () => {
    render(<MapView board={board} players={crowd(21)} />);

    const start = board.cells.find((c) => c.kind === 'start');
    if (!start) throw new Error('No start');
    const chip = screen.getByTestId('more-chip');
    const [, y] =
      /translate\(([-\d.]+) ([-\d.]+)\)/.exec(chip.getAttribute('transform') ?? '')?.slice(1) ?? [];
    // Above the start's label (a circle of about 30 around its centre), as the stickers are
    expect(Number(y)).toBeLessThan(start.y - 30);
    // Two stickers and «+19»: 21 players in all
    expect(chip).toHaveTextContent('+19');
    expect(document.querySelectorAll('[data-player]')).toHaveLength(2);
  });

  it('keeps the zoom buttons at the bottom of the frame on a phone too (K2-4)', () => {
    render(<MapView board={board} players={crowd(1)} tools="auto" />);

    const zoom = screen.getByRole('button', { name: 'Приблизить' }).parentElement;
    expect(zoom).toHaveClass('bottom-3');
    expect(zoom).not.toHaveClass('top-3');
  });

  it('opens a big board around me on a desktop instead of shrinking its cells (K2-3)', () => {
    const me = { x: 300, y: 400 };
    const view = openingView(big, { width: 720, height: 760 }, null, me);

    expect(720 / view.w).toBeGreaterThanOrEqual(camera.readable);
    expect(view.cx).toBeCloseTo(me.x + 0, -3);
  });

  it('fits the whole board above the zoom buttons’ band (K2-4)', () => {
    for (const box of [
      { width: 358, height: 420 },
      { width: 720, height: 760 },
    ]) {
      const view = openingView(board, box, null, null);
      const perPixel = view.w / box.width;
      // The board's bottom edge, in frame pixels from the frame's top, stays above the band
      const bottom = (board.height - (view.cy - (view.w * box.height) / box.width / 2)) / perPixel;
      expect(bottom).toBeLessThanOrEqual(box.height - camera.tools + 1);
    }
  });

  it('still shows a small board whole on a desktop', () => {
    const view = openingView(board, { width: 720, height: 760 }, null, null);

    expect(view.w).toBeGreaterThanOrEqual(Math.max(board.width, board.height * (720 / 760)));
  });

  it('lets a phone show the stickers that fan out from a cell at the board’s edge (K2-2)', () => {
    const start = board.cells.find((c) => c.kind === 'start');
    if (!start) throw new Error('No start');
    const view = openingView(board, { width: 358, height: 420 }, start, start);

    // The leftmost sticker stands about 60 world units left of the start: the view reaches it
    expect(view.cx - view.w / 2).toBeLessThanOrEqual(start.x - 60);
  });
});
