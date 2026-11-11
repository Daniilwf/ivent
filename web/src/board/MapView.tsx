import { Flag, LocateFixed, Minus, Orbit, Plus, ShoppingBag, Sparkles, X } from 'lucide-react';
import {
  useEffect,
  useEffectEvent,
  useId,
  useImperativeHandle,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type PointerEvent,
  type ReactNode,
  type Ref,
} from 'react';
import { playerToken } from '../design/players';
import { ru } from '../i18n/ru';
import { IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import { useDesk } from '../ui/useDesk';
import { blobPath, cellById, polylinePath, zoneAt } from './geometry';
import { zoneFill } from './graphBoard';
import type { Board, BoardBackground, BoardZone, Player, Point, ZoneTheme } from './types';

const t = ru.board;

// ---- Scenery: seeded, so the same board always looks the same ----
function seeded(seed: number) {
  let s = seed;
  return () => {
    s |= 0;
    s = (s + 0x6d2b79f5) | 0;
    let r = Math.imul(s ^ (s >>> 15), 1 | s);
    r = (r + Math.imul(r ^ (r >>> 7), 61 | r)) ^ r;
    return ((r ^ (r >>> 14)) >>> 0) / 4294967296;
  };
}

type Prop = Point & { theme: ZoneTheme; size: number; flip: boolean };

function scenery(board: Board): Prop[] {
  // A picture under the map is its scenery (D-277)
  if (board.background) return [];
  const random = seeded(7);
  const props: Prop[] = [];
  const nearRoute = (p: Point) => board.cells.some((c) => Math.hypot(c.x - p.x, c.y - p.y) < 42);
  for (let i = 0; i < 2600 && props.length < 190; i++) {
    const p = { x: 30 + random() * (board.width - 60), y: 30 + random() * (board.height - 60) };
    if (nearRoute(p) || props.some((q) => Math.hypot(q.x - p.x, q.y - p.y) < 30)) continue;
    const zone = zoneAt(board.zones, p);
    if (!zone) continue;
    props.push({ ...p, theme: zone.theme, size: 0.8 + random() * 0.5, flip: random() > 0.5 });
  }
  return props;
}

const ink = 'var(--color-ink)';

function PropShape({ prop }: { prop: Prop }) {
  const { x, y, size: k } = prop;
  switch (prop.theme) {
    case 'forest':
      return (
        <g transform={`translate(${x} ${y}) scale(${k})`}>
          <rect x={-2.5} y={4} width={5} height={10} fill={ink} />
          <circle r={12} fill="var(--color-table-deep)" stroke={ink} strokeWidth={2.5} />
          <circle cx={-4} cy={-4} r={4} fill="var(--color-zone-meadow)" opacity={0.7} />
        </g>
      );
    case 'mountains':
      return (
        <g transform={`translate(${x} ${y}) scale(${k * (prop.flip ? -1 : 1)} ${k})`}>
          <path
            d="M-18 12 L-2 -16 L16 12 Z"
            fill="var(--color-zone-mountains)"
            stroke={ink}
            strokeWidth={2.5}
            strokeLinejoin="round"
          />
          <path d="M-8 -4 L-2 -16 L5 -4 L1 -1 L-3 -5 Z" fill="var(--color-snow)" />
        </g>
      );
    case 'swamp':
      return (
        <g
          transform={`translate(${x} ${y}) scale(${k})`}
          stroke={ink}
          strokeWidth={2}
          fill="none"
          strokeLinecap="round"
        >
          <ellipse rx={13} ry={5} fill="var(--color-table-dots)" opacity={0.6} />
          <path d="M-6 0 V-14 M0 0 V-18 M6 0 V-12" />
          <ellipse cx={0} cy={-19} rx={2} ry={4} fill={ink} />
        </g>
      );
    case 'city':
      return (
        <g
          transform={`translate(${x} ${y}) scale(${k})`}
          stroke={ink}
          strokeWidth={2.2}
          strokeLinejoin="round"
        >
          <rect x={-10} y={-6} width={20} height={16} fill="var(--color-card)" />
          <path d="M-13 -6 L0 -18 L13 -6 Z" fill="var(--color-table-deep)" />
          <rect x={-3} y={2} width={6} height={8} fill={ink} />
        </g>
      );
    case 'meadow':
      return (
        <g transform={`translate(${x} ${y}) scale(${k})`}>
          <circle r={5} fill="var(--color-card)" stroke={ink} strokeWidth={1.5} />
          <circle r={2} fill="var(--color-zone-city)" />
        </g>
      );
    default:
      return null;
  }
}

/**
 * The admin's picture under the map (D-277). It comes after the season itself — the map is drawn first, the picture
 * fades in once the browser has it, so a slow phone network never waits for it.
 */
function BackgroundPicture({ picture }: { picture: BoardBackground }) {
  // Loaded aside, so a picture already in the cache still says it is there (an SVG image's own load event may come
  // before the page listens to it)
  const [ready, setReady] = useState<string | null>(null);
  useEffect(() => {
    let active = true;
    const probe = new Image();
    probe.onload = () => {
      if (active) setReady(picture.url);
    };
    probe.src = picture.url;
    return () => {
      active = false;
    };
  }, [picture.url]);
  return (
    <image
      href={picture.url}
      x={0}
      y={0}
      width={picture.width}
      height={picture.height}
      preserveAspectRatio="none"
      data-map-background=""
      className={cx(
        'transition-opacity duration-(--duration-slow) motion-reduce:transition-none',
        ready === picture.url ? 'opacity-100' : 'opacity-0',
      )}
    />
  );
}

function Castle({ at }: { at: Point }) {
  return (
    <g
      transform={`translate(${at.x + 34} ${at.y - 6})`}
      stroke={ink}
      strokeWidth={3}
      strokeLinejoin="round"
    >
      <rect x={-22} y={-20} width={44} height={40} fill="var(--color-card)" />
      <rect x={-34} y={-38} width={16} height={58} fill="var(--color-card)" />
      <rect x={18} y={-38} width={16} height={58} fill="var(--color-card)" />
      <path d="M-34 -38 h4 v-6 h4 v6 h4 v-6 h4 v6" fill="none" />
      <path d="M18 -38 h4 v-6 h4 v6 h4 v-6 h4 v6" fill="none" />
      <path d="M-6 20 v-14 a6 6 0 0 1 12 0 v14" fill={ink} />
      <path d="M26 -38 V-62 L44 -55 L26 -48" fill="var(--color-gold)" />
    </g>
  );
}

// ---- Stickers on the map ----
export function MapSticker({
  player,
  at,
  size = 36,
  index = 0,
  count = 1,
  lift = 32,
}: {
  player: Player;
  at: Point;
  size?: number;
  index?: number;
  count?: number;
  /** How far above the cell's centre the sticker stands: more over the start and the finish */
  lift?: number;
}) {
  const r = size / 2;
  // Several players on one cell fan out above it instead of piling up
  const angle = count === 1 ? -90 : -150 + (120 / (Math.min(count, 3) - 1)) * index;
  const reach = count === 1 ? lift : lift + 14;
  const dx = at.x + Math.cos((angle * Math.PI) / 180) * reach;
  const dy = at.y + Math.sin((angle * Math.PI) / 180) * reach;
  // Two maps on one page must not share clip ids
  const clip = `sticker-${useId().replace(/:/g, '')}`;
  const token = playerToken(player.token);
  return (
    <g transform={`translate(${dx} ${dy}) rotate(${index % 2 ? 6 : -6})`} data-player={player.id}>
      <title>{player.name}</title>
      <circle r={r + 5} fill={ink} />
      <circle r={r + 3} fill="var(--color-card)" />
      <clipPath id={clip}>
        <circle r={r} />
      </clipPath>
      <circle r={r} fill={token.fill} />
      {player.avatar ? (
        <image
          href={player.avatar}
          x={-r}
          y={-r}
          width={size}
          height={size}
          clipPath={`url(#${clip})`}
          preserveAspectRatio="xMidYMid slice"
        />
      ) : (
        <text
          className="pointer-events-none font-display font-heavy"
          textAnchor="middle"
          dominantBaseline="central"
          style={{ fill: token.ink, fontSize: size * 0.48 }}
        >
          {player.name.slice(0, 1)}
        </text>
      )}
      {player.me ? (
        <g>
          <circle r={r + 7} fill="none" stroke="var(--color-me)" strokeWidth={4} />
          <rect x={-16} y={-r - 24} width={32} height={18} rx={9} fill="var(--color-me)" />
          <text
            y={-r - 15}
            className="pointer-events-none font-display text-xs font-bold"
            textAnchor="middle"
            dominantBaseline="central"
            fill="var(--color-on-color)"
          >
            {t.you}
          </text>
        </g>
      ) : null}
    </g>
  );
}

/**
 * A label squeezed to `width` once it is wider (K1M-8: «Финиш» spilled past its circle in the display font). Measured in
 * the browser after the font loads; until then, and where nothing is measured, it stands as it is.
 */
function FitText({ width, children }: { width: number; children: string }) {
  const ref = useRef<SVGTSpanElement>(null);
  const [squeeze, setSqueeze] = useState(false);
  useEffect(() => {
    const measure = () => {
      const el = ref.current;
      if (el && typeof el.getComputedTextLength === 'function')
        setSqueeze(el.getComputedTextLength() > width);
    };
    measure();
    if ('fonts' in document) void document.fonts.ready.then(measure);
  }, [width, children]);
  return (
    <tspan ref={ref} {...(squeeze ? { textLength: width, lengthAdjust: 'spacingAndGlyphs' } : {})}>
      {children}
    </tspan>
  );
}

/** «+N» next to a cell with more players than fit */
function MoreChip({ at, count, radius }: { at: Point; count: number; radius: number }) {
  // Past the rim of a big cell (the start, the finish), so it never covers the name (K1M-8)
  const dx = radius > 18 ? radius + 21 : 30;
  return (
    <g transform={`translate(${at.x + dx} ${at.y + 4})`} data-more="">
      <rect
        x={-17}
        y={-12}
        width={34}
        height={24}
        rx={12}
        fill="var(--color-card)"
        stroke={ink}
        strokeWidth={2.5}
      />
      <text
        className="font-display text-xs font-bold"
        textAnchor="middle"
        dominantBaseline="central"
        fill={ink}
      >
        {t.more(count)}
      </text>
    </g>
  );
}

// ---- Teleports: a dashed arc from the teleport to where it leads, an arrowhead at the end ----
function TeleportArc({ from, to }: { from: Point; to: Point }) {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const length = Math.hypot(dx, dy) || 1;
  // The arc bends to one side by a quarter of its length, so it never runs along the road it jumps over
  const control = {
    x: (from.x + to.x) / 2 - (dy / length) * length * 0.25,
    y: (from.y + to.y) / 2 + (dx / length) * length * 0.25,
  };
  // It starts and ends at the cells' rims, not their centres
  const rim = (p: Point, towards: Point, r: number) => {
    const d = Math.hypot(towards.x - p.x, towards.y - p.y) || 1;
    return { x: p.x + ((towards.x - p.x) / d) * r, y: p.y + ((towards.y - p.y) / d) * r };
  };
  const a = rim(from, control, 22);
  const b = rim(to, control, 26);
  const tx = b.x - control.x;
  const ty = b.y - control.y;
  const t = Math.hypot(tx, ty) || 1;
  const ux = tx / t;
  const uy = ty / t;
  const head = `M${b.x} ${b.y} L${b.x - ux * 14 - uy * 8} ${b.y - uy * 14 + ux * 8} L${b.x - ux * 14 + uy * 8} ${b.y - uy * 14 - ux * 8} Z`;
  return (
    <g data-teleport="" pointerEvents="none">
      <path
        d={`M${a.x} ${a.y} Q${control.x} ${control.y} ${b.x} ${b.y}`}
        fill="none"
        stroke={ink}
        strokeWidth={4}
        strokeDasharray="10 8"
        strokeLinecap="round"
      />
      <path d={head} fill={ink} />
    </g>
  );
}

/** The branches of a fork to choose from: a ring round each option's first cell and its number, as on the buttons */
function OptionMark({ at, number }: { at: Point; number: number }) {
  return (
    <g data-option={number} pointerEvents="none">
      <circle cx={at.x} cy={at.y} r={27} fill="none" stroke="var(--color-me)" strokeWidth={5} />
      <g transform={`translate(${at.x + 24} ${at.y + 24})`}>
        <circle r={15} fill="var(--color-me)" stroke={ink} strokeWidth={2.5} />
        <text
          className="pointer-events-none font-display text-sm font-heavy"
          textAnchor="middle"
          dominantBaseline="central"
          fill="var(--color-on-color)"
        >
          {number}
        </text>
      </g>
    </g>
  );
}

// ---- Zone signs: where they cover no cell and stay inside their zone ----
const signWidth = (text: string) => text.length * 16 * 0.72 + 28;

function signSpots(board: Board): Map<string, Point> {
  const spots = new Map<string, Point>();
  for (const z of board.zones) {
    // A zone without a name (stage 1's decoration) gets no sign
    if (!z.name) continue;
    const half = signWidth(z.name) / 2;
    let best: { p: Point; score: number } | null = null;
    for (let x = 30 + half; x < board.width - 30 - half; x += 10) {
      for (let y = 40; y < board.height - 40; y += 10) {
        if (zoneAt(board.zones, { x, y })?.id !== z.id) continue;
        const corners = [-half, 0, half].flatMap((dx) =>
          [-15, 15].map((dy) => ({ x: x + dx, y: y + dy })),
        );
        if (corners.filter((e) => zoneAt(board.zones, e)?.id !== z.id).length > 2) continue;
        const clear = Math.min(
          ...board.cells.map((c) =>
            Math.hypot(Math.max(Math.abs(c.x - x) - half, 0), Math.max(Math.abs(c.y - y) - 16, 0)),
          ),
        );
        if (!best || clear > best.score) best = { p: { x, y }, score: clear };
        if (clear > 60) break;
      }
    }
    if (best) spots.set(z.id, best.p);
  }
  return spots;
}

// ---- Pan and zoom ----
/** The view is its centre and its width in world units; the height follows the frame's shape */
type View = { cx: number; cy: number; w: number };
type Box = { width: number; height: number };

/**
 * How far past the board's edge the camera may look, in world units: a token fanned out over an edge cell and its «ты»
 * stay whole in the frame (K1M-6)
 */
const edgeRoom = 48;
/** The zoom buttons' strip: 44 px buttons 12 px from the frame's edge, and a little air (K1M-6) */
const toolsRoom = 64;
/** A pointer that moved less than this between down and up tapped (K1M-7) */
const tapSlop = 8;

/**
 * `focus`: open around this point, about one world unit to a pixel of the frame; `anchor`: where a tall frame centres;
 * `toolsAt`: the side of the frame the zoom buttons stand on, kept clear of the board in the first view.
 */
function usePanZoom(
  board: Board,
  focus: Point | null,
  anchor: Point | null,
  toolsAt: 'top' | 'bottom' | 'auto' | 'none',
  onTap: (target: Element | null) => void,
) {
  // null until the user moves the map: the whole world, fitted to the frame
  const [moved, setView] = useState<View | null>(null);
  const [box, setBox] = useState<Box>({ width: board.width, height: board.height });
  const pointers = useRef(new Map<number, Point>());
  // The pointer that went down alone: where, on what, and whether it has moved since (a tap or a drag)
  const press = useRef<{ id: number; at: Point; target: Element | null; moved: boolean } | null>(
    null,
  );
  const svg = useRef<SVGSVGElement | null>(null);

  useEffect(() => {
    const el = svg.current;
    if (!el) return;
    const observer = new ResizeObserver(() => {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) setBox({ width: r.width, height: r.height });
    });
    observer.observe(el);
    return () => {
      observer.disconnect();
    };
  }, []);

  const ratio = box.height / box.width;
  // The zoom buttons stand at the top of a phone's page and at the bottom of a desktop's (tools="auto"): by the
  // page's `desk` breakpoint, as the buttons' own classes, not by the frame's width (a desktop's frame is narrower)
  const desk = useDesk();
  const side = toolsAt === 'none' ? null : toolsAt === 'auto' ? (desk ? 'bottom' : 'top') : toolsAt;
  const gap = side ? toolsRoom : 0;

  // The world the camera may show (K1M-2): the board with room round its edge and, on the buttons' side, the strip
  // they stand on (in world units at the view's zoom)
  const area = (w: number) => {
    const strip = (gap * w) / box.width;
    return {
      x0: -edgeRoom,
      x1: board.width + edgeRoom,
      y0: -edgeRoom - (side === 'top' ? strip : 0),
      y1: board.height + edgeRoom + (side === 'bottom' ? strip : 0),
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

  // The width at which the whole board's height, its room and the buttons' strip fill the frame's height
  const tallWidth =
    ((board.height + edgeRoom * 2) * box.width) / Math.max(box.height - gap, box.height / 2);
  // Only a phone's frame crops the board around me; a desktop's always shows the whole board
  const portrait = ratio > board.height / board.width && box.width < 600;
  // A wide frame shows the whole board; a tall one (a phone) fills its height and centres on me
  const wholeWidth = Math.max(board.width + edgeRoom * 2, tallWidth);
  const fitWidth = portrait ? tallWidth : wholeWidth;
  const fit = clamp({
    cx: portrait ? (anchor?.x ?? board.width / 2) : board.width / 2,
    cy: board.height / 2,
    w: fitWidth,
  });
  const openWidth = Math.min(Math.max(box.width, 320), fit.w);
  const wide = box.width >= 600;
  // Around the focus, but never past the world's edge: my token whole, the buttons over the room past the board
  const opening: View = focus && !wide ? clamp({ w: openWidth, cx: focus.x, cy: focus.y }) : fit;
  const view = clamp(moved ?? opening);
  const current = (v: View | null) => clamp(v ?? opening);
  // The page scrolls under a swipe while the map has nowhere to go up or down (K1M-2: a phone held sideways)
  const world = area(view.w);
  const pansUpDown = view.w * ratio < world.y1 - world.y0 - 1;

  const toWorld = (clientX: number, clientY: number, v: View) => {
    const r = svg.current?.getBoundingClientRect();
    if (!r) return { x: v.cx, y: v.cy };
    const scale = v.w / r.width;
    return {
      x: v.cx - v.w / 2 + (clientX - r.left) * scale,
      y: v.cy - (v.w * ratio) / 2 + (clientY - r.top) * scale,
    };
  };

  const zoomAt = (factor: number, clientX?: number, clientY?: number) => {
    setView((raw) => {
      const v = current(raw);
      // Out as far as the whole world with its room, never farther (K1M-2)
      const w = Math.min(Math.max(v.w * factor, 260), Math.max(wholeWidth, 260));
      const focus =
        clientX !== undefined && clientY !== undefined
          ? toWorld(clientX, clientY, v)
          : { x: v.cx, y: v.cy };
      const k = w / v.w;
      return clamp({ w, cx: focus.x - (focus.x - v.cx) * k, cy: focus.y - (focus.y - v.cy) * k });
    });
  };

  const panBy = (dx: number, dy: number) => {
    setView((raw) => {
      const v = current(raw);
      return clamp({ ...v, cx: v.cx + dx * v.w, cy: v.cy + dy * v.w });
    });
  };

  const onWheel = useEffectEvent((e: globalThis.WheelEvent) => {
    e.preventDefault();
    zoomAt(e.deltaY > 0 ? 1.15 : 0.87, e.clientX, e.clientY);
  });
  useEffect(() => {
    const el = svg.current;
    if (!el) return;
    const listener = (e: globalThis.WheelEvent) => {
      onWheel(e);
    };
    el.addEventListener('wheel', listener, { passive: false });
    return () => {
      el.removeEventListener('wheel', listener);
    };
  }, []);

  const movedFar = (e: PointerEvent) => {
    const p = press.current;
    if (p?.id === e.pointerId && Math.hypot(e.clientX - p.at.x, e.clientY - p.at.y) >= tapSlop)
      p.moved = true;
  };
  const release = (e: PointerEvent, tapped: boolean) => {
    pointers.current.delete(e.pointerId);
    const p = press.current;
    if (p?.id !== e.pointerId) return;
    press.current = null;
    if (tapped && !p.moved) onTap(p.target);
  };

  const handlers = {
    onPointerDown: (e: PointerEvent) => {
      e.currentTarget.setPointerCapture(e.pointerId);
      pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
      // A second finger makes it a pinch, not a tap
      press.current =
        pointers.current.size === 1
          ? {
              id: e.pointerId,
              at: { x: e.clientX, y: e.clientY },
              target: e.target instanceof Element ? e.target : null,
              moved: false,
            }
          : null;
    },
    onPointerMove: (e: PointerEvent) => {
      const previous = pointers.current.get(e.pointerId);
      if (!previous) return;
      movedFar(e);
      const r = svg.current?.getBoundingClientRect();
      if (pointers.current.size === 2) {
        const [a, b] = [...pointers.current.values()] as [Point, Point];
        const before = Math.hypot(a.x - b.x, a.y - b.y);
        pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
        const [c, d] = [...pointers.current.values()] as [Point, Point];
        const after = Math.hypot(c.x - d.x, c.y - d.y);
        if (after > 0) zoomAt(before / after, e.clientX, e.clientY);
        return;
      }
      pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
      setView((raw) => {
        const v = current(raw);
        const scale = r ? v.w / r.width : 1;
        return clamp({
          ...v,
          cx: v.cx - (e.clientX - previous.x) * scale,
          cy: v.cy - (e.clientY - previous.y) * scale,
        });
      });
    },
    onPointerUp: (e: PointerEvent) => {
      movedFar(e);
      release(e, true);
    },
    onPointerCancel: (e: PointerEvent) => {
      release(e, false);
    },
    // The keyboard: arrows move the map, + and − zoom
    onKeyDown: (e: KeyboardEvent) => {
      const step = 0.1;
      const moves: Record<string, () => void> = {
        ArrowLeft: () => {
          panBy(-step, 0);
        },
        ArrowRight: () => {
          panBy(step, 0);
        },
        ArrowUp: () => {
          panBy(0, -step);
        },
        ArrowDown: () => {
          panBy(0, step);
        },
        '+': () => {
          zoomAt(0.8);
        },
        '=': () => {
          zoomAt(0.8);
        },
        '-': () => {
          zoomAt(1.25);
        },
      };
      const move = moves[e.key];
      if (move) {
        e.preventDefault();
        move();
      }
    },
  };

  const centerOn = (p: Point, w = view.w) => {
    setView(clamp({ w, cx: p.x, cy: p.y }));
  };

  // Back to the first view: the whole world, or around me — after a moment moved the camera
  const reset = () => {
    setView(null);
  };

  const viewBox = `${view.cx - view.w / 2} ${view.cy - (view.w * ratio) / 2} ${view.w} ${view.w * ratio}`;
  return { viewBox, svg, handlers, zoomAt, centerOn, reset, pansUpDown };
}

export type MapApi = { centerOn: (p: Point, w?: number) => void; reset: () => void };

export type MapViewProps = {
  board: Board;
  players: Player[];
  className?: string;
  /** Players drawn by the caller (a moving token): left out of the stickers on cells */
  hide?: string[];
  /** The first view: the whole world, or a close look around this cell */
  focus?: number | undefined;
  /** Where the zoom buttons stand: at the bottom on a desktop, at the top over a phone's bottom sheet */
  tools?: 'top' | 'bottom' | 'auto' | 'none';
  children?: ReactNode;
  /** The camera, for a moment that follows a token */
  ref?: Ref<MapApi> | undefined;
  /** A branch choice: the first cells of the branches, numbered as the buttons that pick them */
  options?: number[] | undefined;
};

/** The season's map: a board of zones with the route, the cells and the players' stickers; pan, zoom, keyboard */
export function MapView({
  board,
  players,
  className,
  hide = [],
  focus,
  tools = 'bottom',
  children,
  ref,
  options = [],
}: MapViewProps) {
  const start = focus !== undefined ? cellById(board, focus) : null;
  const meAt = players.find((p) => p.me);
  // The cell whose players a tap named (K1M-7): a phone has no hover for the tokens' titles
  const [picked, setPicked] = useState<number | null>(null);
  const { viewBox, svg, handlers, zoomAt, centerOn, reset, pansUpDown } = usePanZoom(
    board,
    start,
    meAt ? cellById(board, meAt.cell) : null,
    tools,
    (target) => {
      const at = target?.closest('[data-at]')?.getAttribute('data-at');
      setPicked(at === undefined || at === null ? null : Number(at));
    },
  );
  useImperativeHandle(ref, () => ({ centerOn, reset }));
  const props = useMemo(() => scenery(board), [board]);
  const signs = useMemo(() => signSpots(board), [board]);
  const zoneTheme = useMemo(() => new Map(board.zones.map((z) => [z.id, z.theme])), [board]);
  const me = players.find((p) => p.me);
  // My cell is marked only while my sticker stands on it; a moving token is drawn by the caller
  const myCell = me && !hide.includes(me.id) ? me.cell : null;
  const finish = board.cells.find((c) => c.kind === 'finish');
  const onCell = (id: number) => players.filter((p) => p.cell === id && !hide.includes(p.id));
  const pickedCell = picked === null ? undefined : board.cells.find((c) => c.id === picked);
  const pickedPlayers = picked === null ? [] : onCell(picked);

  return (
    <div
      className={cx(
        'table-surface relative overflow-hidden select-none has-[svg:focus-visible]:outline-3 has-[svg:focus-visible]:outline-offset-3 has-[svg:focus-visible]:outline-ink',
        // A swipe the map cannot follow up or down scrolls the page (K1M-2)
        pansUpDown ? 'touch-none' : 'touch-pan-y',
        className,
      )}
    >
      <svg
        ref={svg}
        viewBox={viewBox}
        preserveAspectRatio="xMidYMid meet"
        role="application"
        aria-roledescription={t.roleDescription}
        tabIndex={0}
        aria-label={me ? t.mapLabel(me.cell) : t.mapLabelNobody}
        className="block size-full cursor-grab outline-none active:cursor-grabbing"
        onPointerDown={handlers.onPointerDown}
        onPointerMove={handlers.onPointerMove}
        onPointerUp={handlers.onPointerUp}
        onPointerCancel={handlers.onPointerCancel}
        onKeyDown={handlers.onKeyDown}
      >
        {/* No layer under the map: the table is the frame's own background (a huge SVG layer can exceed a phone
            GPU's texture limit and render in part). Zones carry their own outline: no black underlay either. The
            admin's picture (D-277) is the one exception: one image, at most a few thousand pixels a side. */}
        {board.background ? <BackgroundPicture picture={board.background} /> : null}
        {/* Over a picture the zones are not drawn (D-277): the picture is the world, a computed shape would never
            match its areas. Each cell then carries its zone as a ring, below. */}
        {board.background
          ? null
          : board.zones.map((z: BoardZone) => (
              <path
                key={z.id}
                d={blobPath(z.outline)}
                fill={zoneFill[z.theme]}
                stroke={ink}
                strokeWidth={6}
                strokeLinejoin="round"
                data-zone={z.id}
              />
            ))}
        {props.map((p, i) => (
          <PropShape key={i} prop={p} />
        ))}

        {finish && !board.background ? <Castle at={finish} /> : null}

        {/* The route: a road with an outline, the cells over it */}
        {board.roads.map((road, i) => (
          <path
            key={`edge-${i}`}
            d={polylinePath(road)}
            fill="none"
            stroke={ink}
            strokeWidth={22}
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        ))}
        {board.roads.map((road, i) => (
          <path
            key={`road-${i}`}
            d={polylinePath(road)}
            fill="none"
            stroke="var(--color-route)"
            strokeWidth={15}
            strokeLinecap="round"
            strokeLinejoin="round"
            data-road={i}
          />
        ))}

        {/* The zone signs over the route, so a road never cuts a name (K1M-9) */}
        {board.zones.map((z) => {
          const at = signs.get(z.id);
          if (!at) return null;
          const w = signWidth(z.name);
          return (
            <g key={`sign-${z.id}`} transform={`translate(${at.x} ${at.y})`} data-zone-sign={z.id}>
              <rect x={-w / 2} y={-12} width={w} height={30} rx={15} fill={ink} />
              <rect
                x={-w / 2}
                y={-15}
                width={w}
                height={30}
                rx={15}
                fill="var(--color-card)"
                stroke={ink}
                strokeWidth={2.5}
              />
              <text
                className="pointer-events-none font-display text-base font-heavy"
                textAnchor="middle"
                dominantBaseline="central"
                fill={ink}
              >
                {z.name}
              </text>
            </g>
          );
        })}

        {board.cells.flatMap((c) =>
          c.kind === 'teleport' && c.to !== undefined
            ? [<TeleportArc key={`teleport-${c.id}`} from={c} to={cellById(board, c.to)} />]
            : [],
        )}

        {board.cells.map((c) => {
          const big = c.kind === 'start' || c.kind === 'finish';
          const mine = myCell === c.id;
          const fill = mine
            ? 'var(--color-me)'
            : c.kind === 'event' || c.kind === 'bonus'
              ? 'var(--color-gold)'
              : c.kind === 'checkpoint'
                ? 'var(--color-zone-meadow)'
                : big
                  ? ink
                  : 'var(--color-card)';
          const label = cx(
            'pointer-events-none font-display font-bold',
            big ? 'text-xs' : 'text-sm',
          );
          const ring = board.background ? zoneTheme.get(c.zone ?? '') : undefined;
          return (
            <g key={c.id} data-cell={c.id}>
              {ring ? (
                <circle
                  cx={c.x}
                  cy={c.y}
                  r={(big ? 30 : c.kind === 'fork' ? 24 : 18) + 8}
                  fill={zoneFill[ring]}
                  stroke={ink}
                  strokeWidth={2.5}
                  data-cell-zone={c.zone}
                />
              ) : null}
              {c.kind === 'fork' ? (
                <rect
                  x={c.x - 17}
                  y={c.y - 17}
                  width={34}
                  height={34}
                  rx={6}
                  transform={`rotate(45 ${c.x} ${c.y})`}
                  fill="var(--color-card)"
                  stroke={ink}
                  strokeWidth={3}
                />
              ) : (
                <circle
                  cx={c.x}
                  cy={c.y}
                  r={big ? 30 : 18}
                  fill={fill}
                  stroke={ink}
                  strokeWidth={3}
                />
              )}
              {big ? (
                <text
                  x={c.x}
                  y={c.y}
                  className={label}
                  textAnchor="middle"
                  dominantBaseline="central"
                  fill="var(--color-on-color)"
                >
                  <FitText width={50}>{c.kind === 'start' ? t.start : t.finish}</FitText>
                </text>
              ) : c.kind === 'event' ? (
                <Sparkles
                  x={c.x - 10}
                  y={c.y - 10}
                  width={20}
                  height={20}
                  color={ink}
                  strokeWidth={2.5}
                  aria-hidden
                />
              ) : c.kind === 'teleport' ? (
                <Orbit
                  x={c.x - 10}
                  y={c.y - 10}
                  width={20}
                  height={20}
                  color={mine ? 'var(--color-on-color)' : ink}
                  strokeWidth={2.5}
                  aria-hidden
                />
              ) : c.kind === 'shop' ? (
                <ShoppingBag
                  x={c.x - 10}
                  y={c.y - 10}
                  width={20}
                  height={20}
                  color={mine ? 'var(--color-on-color)' : ink}
                  strokeWidth={2.5}
                  aria-hidden
                />
              ) : c.kind === 'bonus' ? (
                <text
                  x={c.x}
                  y={c.y}
                  className="pointer-events-none font-display text-sm font-heavy"
                  textAnchor="middle"
                  dominantBaseline="central"
                  fill={mine ? 'var(--color-on-color)' : ink}
                >
                  {t.bonus(c.amount ?? 0)}
                </text>
              ) : c.kind === 'checkpoint' ? (
                <Flag
                  x={c.x - 10}
                  y={c.y - 10}
                  width={20}
                  height={20}
                  color={ink}
                  strokeWidth={2.5}
                  aria-hidden
                />
              ) : (
                <text
                  x={c.x}
                  y={c.y}
                  className={label}
                  textAnchor="middle"
                  dominantBaseline="central"
                  fill={mine ? 'var(--color-on-color)' : ink}
                >
                  {c.id}
                </text>
              )}
            </g>
          );
        })}

        {options.map((cell, i) => (
          <OptionMark key={`option-${cell}`} at={cellById(board, cell)} number={i + 1} />
        ))}

        {board.cells.flatMap((c) => {
          const here = onCell(c.id);
          // Me first, so my sticker is never the one hidden behind «+N»
          const ordered = [...here].sort((a, b) => Number(b.me ?? false) - Number(a.me ?? false));
          const big = c.kind === 'start' || c.kind === 'finish';
          // A tap on a token or on «+N» names everybody on the cell (K1M-7)
          return here.length === 0
            ? []
            : [
                <g key={`players-${c.id}`} data-at={c.id} className="cursor-pointer">
                  {ordered.slice(0, 3).map((p, i) => (
                    <MapSticker
                      key={p.id}
                      player={p}
                      at={c}
                      index={i}
                      count={here.length}
                      lift={big ? 52 : 32}
                    />
                  ))}
                  {here.length > 3 ? (
                    <MoreChip at={c} count={here.length - 3} radius={big ? 30 : 18} />
                  ) : null}
                </g>,
              ];
        })}

        {children}
      </svg>

      {pickedCell && pickedPlayers.length > 0 ? (
        <div
          data-testid="map-picked"
          role="status"
          className={cx(
            'absolute right-3 left-3 flex items-start gap-2 rounded-lg border-2 border-ink bg-card py-2 pr-2 pl-3 text-sm text-ink desk:right-auto desk:max-w-sm',
            // Across the frame from the zoom buttons
            tools === 'bottom'
              ? 'top-3'
              : tools === 'auto'
                ? 'bottom-3 desk:top-3 desk:bottom-auto'
                : 'bottom-3',
          )}
        >
          <p className="min-w-0">
            <span className="font-bold">
              {pickedCell.kind === 'start'
                ? t.start
                : pickedCell.kind === 'finish'
                  ? t.finish
                  : ru.map.cellNumber(pickedCell.id)}
            </span>
            {': '}
            {pickedPlayers.map((p) => p.name).join(', ')}
          </p>
          <IconButton
            label={t.pickedClose}
            className="size-8"
            onClick={() => {
              setPicked(null);
            }}
          >
            <X size={16} />
          </IconButton>
        </div>
      ) : null}

      {tools === 'none' ? null : (
        <div
          className={cx(
            'absolute right-3 flex gap-2',
            tools === 'top'
              ? 'top-3'
              : tools === 'bottom'
                ? 'bottom-3'
                : 'top-3 desk:top-auto desk:bottom-3',
          )}
        >
          <IconButton
            label={t.zoomIn}
            onClick={() => {
              zoomAt(0.8);
            }}
          >
            <Plus size={20} />
          </IconButton>
          <IconButton
            label={t.zoomOut}
            onClick={() => {
              zoomAt(1.25);
            }}
          >
            <Minus size={20} />
          </IconButton>
          {me ? (
            <IconButton
              label={t.toMe}
              onClick={() => {
                centerOn(cellById(board, me.cell), 380);
              }}
            >
              <LocateFixed size={20} />
            </IconButton>
          ) : null}
        </div>
      )}
    </div>
  );
}
