import { fireEvent, render, screen, within } from '@testing-library/react';
import { onDesktop } from '../test/desk';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { graphBoard } from './graphBoard';
import { linearBoard, type ChainCell } from './linearBoard';
import { MapView } from './MapView';
import type { Board, Player } from './types';

// The map's camera and taps (K1M exploratory testing of stages 1 and 2): pan and zoom keep the board in the frame, a
// phone's first view shows my token clear of the zoom buttons, a tap names the players on a cell, labels stay readable.

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
    render(<MapView board={board} players={crowd(3, 10)} focus={10} tools="auto" />);

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
    render(<MapView board={board} players={crowd(3, 10)} focus={10} tools="auto" />);

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
    render(<MapView board={dev()} players={crowd(3, 10)} focus={10} tools="auto" />);
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
    render(<MapView board={board} players={crowd(21, 0)} focus={0} tools="auto" />);

    const mine = onScreen(stickerAt('p0'));
    const reach = 25 * mine.scale;
    expect(mine.x - reach).toBeGreaterThanOrEqual(0);
    expect(mine.x + reach).toBeLessThanOrEqual(frame.width);
    // «ты» stands above my token
    expect(mine.y - 49 * mine.scale).toBeGreaterThanOrEqual(0);

    // The zoom buttons: three of 44 px with 8 px between, 12 px from the top right corner
    const tools = { left: frame.width - 12 - 148, right: frame.width - 12, top: 12, bottom: 56 };
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
    for (const p of ['p0', 'p1', 'p2']) expect(covered(stickerAt(p), 25), p).toBe(false);
  });

  it('keeps the board clear of the zoom buttons at the bottom of a desktop frame narrower than 900 px (K1M-6)', () => {
    // At 1440×900 the map stands in the second column of the season screen: a frame of about 730 × 760 px
    onDesktop();
    frame = { width: 730, height: 760 };
    const board = dev();
    render(<MapView board={board} players={crowd(3, 10)} focus={10} tools="auto" />);

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

    // Closed by its button
    fireEvent.click(within(picked).getByRole('button', { name: ru.board.pickedClose }));
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
