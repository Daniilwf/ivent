import { fireEvent, render, screen, within } from '@testing-library/react';
import { onDesktop } from '../test/desk';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { graphBoard } from './graphBoard';
import { linearBoard, type ChainCell } from './linearBoard';
import { camera, mapLabel, openingView } from './camera';
import { MapView } from './MapView';
import type { Board, Player } from './types';

// The map's camera and taps (K1M exploratory testing of stages 1 and 2): pan and zoom keep the board in the frame, a
// phone's first view shows my token clear of the zoom buttons, a tap names the players on a cell, labels stay readable.
// Then the map's frame after the design review of stages 1–2 (K2-2…K2-4, D-641).

const chain = (n: number): ChainCell[] =>
  Array.from({ length: n }, (_, i) => ({
    id: `c${i}`,
    type: i === 0 ? 'start' : i === n - 1 ? 'finish' : 'empty',
  }));

const names = ['Лиса', 'Барсук', 'Бобр', 'Вася', 'Волк', 'Ворон', 'Выдра', 'Сова', 'Сокол', 'Лось'];
const crowd = (count: number, cell: number): Player[] =>
  Array.from({ length: count }, (_, i) => ({
    id: `p${i}`,
    name: names[i % names.length] ?? `Игрок ${i}`,
    token: i,
    cell,
    points: 0,
    me: i === 0,
  }));

let frame = { width: 390, height: 420 };

beforeEach(() => {
  frame = { width: 390, height: 420 };
  // The frame measures itself: a ResizeObserver that reports at once, an SVG of the frame's size
  vi.stubGlobal(
    'ResizeObserver',
    class {
      private readonly callback: () => void;
      constructor(callback: () => void) {
        this.callback = callback;
      }
      observe() {
        this.callback();
      }
      disconnect() {}
      unobserve() {}
    },
  );
  vi.spyOn(SVGSVGElement.prototype, 'getBoundingClientRect').mockImplementation(() => ({
    left: 0,
    top: 0,
    right: frame.width,
    bottom: frame.height,
    width: frame.width,
    height: frame.height,
    x: 0,
    y: 0,
    toJSON: () => ({}),
  }));
  Element.prototype.setPointerCapture = vi.fn();
  // jsdom lays out no text: a label measures 0 unless a test says otherwise
  if (!('getComputedTextLength' in SVGElement.prototype))
    Object.defineProperty(SVGElement.prototype, 'getComputedTextLength', {
      configurable: true,
      writable: true,
      value: () => 0,
    });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function viewBox() {
  const [x, y, w, h] = (screen.getByRole('application').getAttribute('viewBox') ?? '')
    .split(' ')
    .map(Number) as [number, number, number, number];
  return { x, y, w, h };
}

/** Where a world point is in the frame's pixels */
function onScreen(p: { x: number; y: number }) {
  const v = viewBox();
  const scale = frame.width / v.w;
  return { x: (p.x - v.x) * scale, y: (p.y - v.y) * scale, scale };
}

function stickerAt(player: string) {
  const g = document.querySelector(`[data-player="${player}"]`);
  const m = /translate\(([-\d.e]+) ([-\d.e]+)\)/.exec(g?.getAttribute('transform') ?? '');
  return { x: Number(m?.[1]), y: Number(m?.[2]) };
}

function drag(dx: number, dy: number, times = 1) {
  const svg = screen.getByRole('application');
  for (let i = 0; i < times; i++) {
    fireEvent.pointerDown(svg, { pointerId: 1, clientX: 200, clientY: 200 });
    fireEvent.pointerMove(svg, { pointerId: 1, clientX: 200 + dx, clientY: 200 + dy });
    fireEvent.pointerUp(svg, { pointerId: 1, clientX: 200 + dx, clientY: 200 + dy });
  }
}

const dev = () => linearBoard(chain(71)).board;

describe('the camera of the map', () => {
  it('keeps the board in the frame however far the map is dragged (K1M-2)', () => {
    const board = dev();
    render(<MapView board={board} players={crowd(3, 10)} focus={10} />);

    drag(2000, 2000, 6);
    let v = viewBox();
    expect(v.x).toBeLessThan(board.width);
    expect(v.y).toBeLessThan(board.height);
    expect(v.x + v.w).toBeGreaterThan(0);
    expect(v.y + v.h).toBeGreaterThan(0);
    // The way back is as long as the way there was: none of the drag past the edge is stored
    const before = v;
    drag(-50, 0);
    v = viewBox();
    expect(v.x).toBeGreaterThan(before.x);

    drag(-3000, -3000, 6);
    v = viewBox();
    expect(v.x).toBeLessThan(board.width);
    expect(v.y).toBeLessThan(board.height);
    expect(v.x + v.w).toBeGreaterThan(0);
    expect(v.y + v.h).toBeGreaterThan(0);
  });

  it('shows the whole board, centred, when zoomed all the way out (K1M-2)', async () => {
    const board = dev();
    render(<MapView board={board} players={crowd(3, 10)} focus={10} />);

    for (let i = 0; i < 8; i++)
      await userEvent.click(screen.getByRole('button', { name: ru.board.zoomOut }));

    const v = viewBox();
    expect(v.x).toBeLessThanOrEqual(0);
    expect(v.y).toBeLessThanOrEqual(0);
    expect(v.x + v.w).toBeGreaterThanOrEqual(board.width);
    expect(v.y + v.h).toBeGreaterThanOrEqual(board.height);
    expect(Math.abs(v.x + v.w / 2 - board.width / 2)).toBeLessThan(1);
  });

  it('lets a swipe scroll the page while the whole height of the board is in the frame (K1M-2)', async () => {
    // A phone held sideways: the map shows the whole board, a vertical swipe is the page's
    frame = { width: 844, height: 414 };
    render(<MapView board={dev()} players={crowd(3, 10)} focus={10} />);
    const surface = screen.getByRole('application').parentElement as HTMLElement;
    expect(surface).toHaveClass('touch-pan-y');
    expect(surface).not.toHaveClass('touch-none');

    // Zoomed in, the map has somewhere to go up and down: the swipe is the map's again
    for (let i = 0; i < 4; i++)
      await userEvent.click(screen.getByRole('button', { name: ru.board.zoomIn }));
    expect(surface).toHaveClass('touch-none');
  });

  it('opens on a phone with my token whole in the frame and clear of the zoom buttons (K1M-6)', () => {
    // The dev season: 21 players on the start, me among them
    const board = dev();
    render(<MapView board={board} players={crowd(21, 0)} focus={0} />);

    const mine = onScreen(stickerAt('p0'));
    const reach = 25 * mine.scale;
    expect(mine.x - reach).toBeGreaterThanOrEqual(0);
    expect(mine.x + reach).toBeLessThanOrEqual(frame.width);
    // «ты» stands above my token
    expect(mine.y - 49 * mine.scale).toBeGreaterThanOrEqual(0);

    // The zoom buttons: three of 44 px with 8 px between, 12 px from the bottom right corner (K2-4)
    const tools = {
      left: frame.width - 12 - 148,
      right: frame.width - 12,
      top: frame.height - 56,
      bottom: frame.height - 12,
    };
    const covered = (p: { x: number; y: number }, r: number) => {
      const s = onScreen(p);
      return (
        s.x + r * s.scale > tools.left &&
        s.x - r * s.scale < tools.right &&
        s.y + r * s.scale > tools.top &&
        s.y - r * s.scale < tools.bottom
      );
    };
    // With the buttons at the bottom (K2-4, D-647) the cells under them are the way on, a drag away; my cell, my
    // stickers and «+N» stand clear of them
    const start = board.cells.find((c) => c.kind === 'start') as Board['cells'][number];
    expect(covered(start, 30), 'the start').toBe(false);
    for (const p of ['p0', 'p1']) expect(covered(stickerAt(p), 25), p).toBe(false);
    const chip = /translate(([-d.e]+) ([-d.e]+))/.exec(
      document.querySelector('[data-more]')?.getAttribute('transform') ?? '',
    );
    expect(covered({ x: Number(chip?.[1]), y: Number(chip?.[2]) }, 17), '«+N»').toBe(false);
  });

  it('keeps every cell clear of the zoom buttons when a phone shows the whole board (K1M-6)', () => {
    // A board that reads whole in a phone's frame opens whole, without a token of mine: no cell under the buttons
    const board = linearBoard(chain(12)).board;
    render(<MapView board={board} players={crowd(3, 5).map((p) => ({ ...p, me: false }))} />);
    // A phone's whole view fills the frame's height with the board (K2-3)
    expect(viewBox().h).toBeGreaterThanOrEqual(board.height);

    const tools = {
      left: frame.width - 12 - 148,
      right: frame.width - 12,
      top: frame.height - 56,
      bottom: frame.height - 12,
    };
    for (const c of board.cells) {
      const at = onScreen(c);
      const r = 30 * at.scale;
      const under =
        at.x + r > tools.left &&
        at.x - r < tools.right &&
        at.y + r > tools.top &&
        at.y - r < tools.bottom;
      expect(under, `cell ${String(c.id)}`).toBe(false);
    }
  });

  it('keeps the board clear of the zoom buttons at the bottom of a desktop frame narrower than 900 px (K1M-6)', () => {
    // At 1440×900 the map stands in the second column of the season screen: a frame of about 730 × 760 px. A board
    // that reads whole there (a 70-cell one opens closer since the readable scale, D-660) is shown whole
    onDesktop();
    frame = { width: 730, height: 760 };
    const board = linearBoard(chain(30)).board;
    render(<MapView board={board} players={crowd(3, 10)} focus={10} />);
    expect(viewBox().w).toBeGreaterThanOrEqual(board.width);

    // The world reaches below the board by the buttons' strip, not above it
    const v = viewBox();
    const scale = frame.width / v.w;
    expect(v.y + v.h).toBeGreaterThanOrEqual(board.height + 64 / scale);

    // The zoom buttons: three of 44 px with 8 px between, 12 px from the bottom right corner
    const tools = {
      left: frame.width - 12 - 148,
      right: frame.width - 12,
      top: frame.height - 56,
      bottom: frame.height - 12,
    };
    const covered = (p: { x: number; y: number }, r: number) => {
      const s = onScreen(p);
      return (
        s.x + r * s.scale > tools.left &&
        s.x - r * s.scale < tools.right &&
        s.y + r * s.scale > tools.top &&
        s.y - r * s.scale < tools.bottom
      );
    };
    for (const c of board.cells) expect(covered(c, 30), `cell ${c.id}`).toBe(false);
  });
});

describe('the players on the map', () => {
  it('names the player of a tapped token (K1M-7)', () => {
    render(<MapView board={dev()} players={crowd(2, 5)} focus={5} />);
    expect(screen.queryByTestId('map-picked')).toBeNull();

    const token = document.querySelector('[data-player="p1"]') as Element;
    fireEvent.pointerDown(token, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerUp(screen.getByRole('application'), {
      pointerId: 1,
      clientX: 101,
      clientY: 100,
    });

    const picked = screen.getByTestId('map-picked');
    expect(picked).toHaveTextContent(ru.map.cellNumber(5));
    expect(picked).toHaveTextContent('Лиса');
    expect(picked).toHaveTextContent('Барсук');

    // Closed by its button, a tap target of 44 px on a phone too (DESIGN.md): no size class but the button's own
    const close = within(picked).getByRole('button', { name: ru.board.pickedClose });
    expect(close.className.split(' ').filter((c) => c.startsWith('size-'))).toEqual(['size-11']);
    fireEvent.click(close);
    expect(screen.queryByTestId('map-picked')).toBeNull();
  });

  it('lists everybody behind «+N» when the chip is tapped (K1M-7)', () => {
    render(<MapView board={dev()} players={crowd(10, 0)} focus={0} />);

    const chip = document.querySelector('[data-more]') as Element;
    fireEvent.pointerDown(chip, { pointerId: 1, clientX: 50, clientY: 50 });
    fireEvent.pointerUp(screen.getByRole('application'), {
      pointerId: 1,
      clientX: 50,
      clientY: 50,
    });

    const picked = screen.getByTestId('map-picked');
    expect(picked).toHaveTextContent(ru.board.start);
    for (const name of names) expect(picked).toHaveTextContent(name);
  });

  it('does not take a drag that starts on a token for a tap', () => {
    render(<MapView board={dev()} players={crowd(2, 5)} focus={5} />);

    const token = document.querySelector('[data-player="p1"]') as Element;
    fireEvent.pointerDown(token, { pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(screen.getByRole('application'), {
      pointerId: 1,
      clientX: 160,
      clientY: 100,
    });
    fireEvent.pointerUp(screen.getByRole('application'), {
      pointerId: 1,
      clientX: 160,
      clientY: 100,
    });

    expect(screen.queryByTestId('map-picked')).toBeNull();
  });
});

describe('the labels of the map', () => {
  it('puts «+N» beside the start, not over its name (K1M-8)', () => {
    const board = dev();
    render(<MapView board={board} players={crowd(10, 0)} />);
    const start = board.cells.find((c) => c.kind === 'start') as Board['cells'][number];

    const chip = document.querySelector('[data-more]');
    const m = /translate\(([-\d.]+) /.exec(chip?.getAttribute('transform') ?? '');
    const left = Number(m?.[1]) - 17;
    expect(left).toBeGreaterThanOrEqual(start.x + 30);
  });

  it('squeezes the name of the start or the finish into its circle when it is wider (K1M-8)', () => {
    vi.spyOn(
      SVGElement.prototype as SVGElement & { getComputedTextLength: () => number },
      'getComputedTextLength',
    ).mockReturnValue(64);
    render(<MapView board={dev()} players={[]} />);

    const finish = screen.getByText(ru.board.finish);
    expect(Number(finish.getAttribute('textLength'))).toBeLessThanOrEqual(50);
    expect(finish).toHaveAttribute('lengthAdjust', 'spacingAndGlyphs');
  });

  it('leaves a name that fits as it is', () => {
    vi.spyOn(
      SVGElement.prototype as SVGElement & { getComputedTextLength: () => number },
      'getComputedTextLength',
    ).mockReturnValue(40);
    render(<MapView board={dev()} players={[]} />);

    expect(screen.getByText(ru.board.start)).not.toHaveAttribute('textLength');
  });

  it('draws the zone signs over the route (K1M-9)', () => {
    const { board } = graphBoard({
      cells: [
        { id: 'start', type: 'start', x: 0, y: 0 },
        { id: 'a', type: 'empty', x: 200, y: 0, zone: 'z' },
        { id: 'b', type: 'empty', x: 400, y: 0, zone: 'z' },
        { id: 'finish', type: 'finish', x: 600, y: 0 },
      ],
      edges: [
        { from: 'start', to: 'a', isDefaultForward: true, isPrimaryBackward: true },
        { from: 'a', to: 'b', isDefaultForward: true, isPrimaryBackward: true },
        { from: 'b', to: 'finish', isDefaultForward: true, isPrimaryBackward: true },
      ],
      zones: [{ id: 'z', name: 'Болото ужаса' }],
    });
    render(<MapView board={board} players={[]} />);

    const sign = document.querySelector('[data-zone-sign="z"]');
    const roads = document.querySelectorAll('[data-road]');
    const lastRoad = roads[roads.length - 1] as Element;
    expect(sign).not.toBeNull();
    expect(
      lastRoad.compareDocumentPosition(sign as Element) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });
});

const firstChain = Array.from({ length: 30 }, (_, i) => ({
  id: `c${String(i)}`,
  type: i === 0 ? ('start' as const) : i === 29 ? ('finish' as const) : ('empty' as const),
}));
const { board: small } = linearBoard(firstChain);

const crowdOnFirst = (count: number): Player[] =>
  Array.from({ length: count }, (_, i) => ({
    id: `p${String(i)}`,
    name: `Игрок ${String(i)}`,
    token: i + 1,
    // The start: cell 0, as the admin counts it (K1M-1)
    cell: 0,
    points: 0,
    me: i === 0,
  }));

/** A big world, as a season's map over a picture is (the demo map is 2070 × 1520) */
const big: Board = { ...small, width: 2070, height: 1520 };

describe('the frame of the map (design review)', () => {
  it('puts «+N» in the stickers’ fan above a crowded start, not over its label (K2-2)', () => {
    render(<MapView board={small} players={crowdOnFirst(21)} />);

    const start = small.cells.find((c) => c.kind === 'start');
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
    render(<MapView board={small} players={crowdOnFirst(1)} />);

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
    // A board that reads whole in both frames (a bigger one opens closer, D-660)
    const few = linearBoard(chain(12)).board;
    for (const box of [
      { width: 358, height: 420 },
      { width: 720, height: 760 },
    ]) {
      const view = openingView(few, box, null, null);
      expect((view.w * box.height) / box.width).toBeGreaterThanOrEqual(few.height);
      const perPixel = view.w / box.width;
      // The board's bottom edge, in frame pixels from the frame's top, stays above the band
      const bottom = (few.height - (view.cy - (view.w * box.height) / box.width / 2)) / perPixel;
      expect(bottom).toBeLessThanOrEqual(box.height - camera.tools + 1);
    }
  });

  it('opens closer than the whole board where its labels would read under the smallest size (D-660)', () => {
    // The linear board of 70 cells on a desktop and on a phone, with my token, without one (a spectator, the admin)
    const board = dev();
    const start = board.cells.find((c) => c.kind === 'start') as Board['cells'][number];
    for (const box of [
      { width: 720, height: 760 },
      { width: 358, height: 420 },
    ]) {
      for (const [focus, anchor] of [
        [null, null],
        [null, start],
        [board.cells[40] ?? null, board.cells[40] ?? null],
      ] as const) {
        const view = openingView(board, box, focus, anchor);
        expect(
          (box.width / view.w) * mapLabel.world,
          `${String(box.width)} px`,
        ).toBeGreaterThanOrEqual(mapLabel.min);
      }
      // Without a token of mine the close look is round the start
      const spectator = openingView(board, box, null, null);
      expect(Math.abs(spectator.cx - start.x)).toBeLessThanOrEqual(spectator.w / 2);
      expect(Math.abs(spectator.cy - start.y)).toBeLessThanOrEqual(
        (spectator.w * box.height) / box.width / 2,
      );
    }
  });

  it('still shows a small board whole on a desktop', () => {
    const view = openingView(small, { width: 720, height: 760 }, null, null);

    expect(view.w).toBeGreaterThanOrEqual(Math.max(small.width, small.height * (720 / 760)));
  });

  it('lets a phone show the stickers that fan out from a cell at the board’s edge (K2-2)', () => {
    const start = small.cells.find((c) => c.kind === 'start');
    if (!start) throw new Error('No start');
    const view = openingView(small, { width: 358, height: 420 }, start, start);

    // The leftmost sticker stands about 60 world units left of the start: the view reaches it
    expect(view.cx - view.w / 2).toBeLessThanOrEqual(start.x - 60);
  });
});
