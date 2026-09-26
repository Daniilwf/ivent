import { Flag, LocateFixed, Minus, Plus, Sparkles } from 'lucide-react';
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type PointerEvent,
  type ReactNode,
  type WheelEvent,
} from 'react';
import { playerToken } from '../design/players';
import { ru } from '../i18n/ru';
import { IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import { blobPath, cellById, polylinePath, zoneAt } from './geometry';
import type { Board, BoardZone, Player, Point, ZoneTheme } from './types';

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
          <path d="M-13 -6 L0 -18 L13 -6 Z" fill="var(--color-action)" />
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

const zoneFill: Record<ZoneTheme, string> = {
  meadow: 'var(--color-zone-meadow)',
  forest: 'var(--color-zone-forest)',
  mountains: 'var(--color-zone-mountains)',
  swamp: 'var(--color-zone-swamp)',
  city: 'var(--color-zone-city)',
  castle: 'var(--color-zone-castle)',
};

// ---- Stickers on the map ----
export function MapSticker({
  player,
  at,
  size = 36,
  index = 0,
  count = 1,
}: {
  player: Player;
  at: Point;
  size?: number;
  index?: number;
  count?: number;
}) {
  const r = size / 2;
  // Several players on one cell fan out above it instead of piling up
  const angle = count === 1 ? -90 : -150 + (120 / (Math.min(count, 3) - 1)) * index;
  const reach = count === 1 ? 32 : 36;
  const dx = at.x + Math.cos((angle * Math.PI) / 180) * reach;
  const dy = at.y + Math.sin((angle * Math.PI) / 180) * reach;
  const clip = `sticker-clip-${player.id}`;
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

/** «+N» next to a cell with more players than fit */
function MoreChip({ at, count }: { at: Point; count: number }) {
  return (
    <g transform={`translate(${at.x + 30} ${at.y + 4})`}>
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

// ---- Zone signs: where they cover no cell and stay inside their zone ----
const signWidth = (text: string) => text.length * 16 * 0.72 + 28;

function signSpots(board: Board): Map<string, Point> {
  const spots = new Map<string, Point>();
  for (const z of board.zones) {
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

function usePanZoom(board: Board, initial: View | null) {
  // null until the user moves the map: the whole world, fitted to the frame
  const [moved, setView] = useState<View | null>(initial);
  const [box, setBox] = useState<Box>({ width: board.width, height: board.height });
  const pointers = useRef(new Map<number, Point>());
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
  const fit: View = {
    cx: board.width / 2,
    cy: board.height / 2,
    w: Math.max(board.width, board.height / ratio) * 1.04,
  };
  const view = moved ?? fit;
  const current = (v: View | null) => v ?? fit;

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
      const w = Math.min(Math.max(v.w * factor, 260), fit.w * 1.2);
      const focus =
        clientX !== undefined && clientY !== undefined
          ? toWorld(clientX, clientY, v)
          : { x: v.cx, y: v.cy };
      const k = w / v.w;
      return { w, cx: focus.x - (focus.x - v.cx) * k, cy: focus.y - (focus.y - v.cy) * k };
    });
  };

  const panBy = (dx: number, dy: number) => {
    setView((raw) => {
      const v = current(raw);
      return { ...v, cx: v.cx + dx * v.w, cy: v.cy + dy * v.w };
    });
  };

  const handlers = {
    onWheel: (e: WheelEvent) => {
      zoomAt(e.deltaY > 0 ? 1.15 : 0.87, e.clientX, e.clientY);
    },
    onPointerDown: (e: PointerEvent) => {
      e.currentTarget.setPointerCapture(e.pointerId);
      pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    },
    onPointerMove: (e: PointerEvent) => {
      const previous = pointers.current.get(e.pointerId);
      if (!previous) return;
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
        return {
          ...v,
          cx: v.cx - (e.clientX - previous.x) * scale,
          cy: v.cy - (e.clientY - previous.y) * scale,
        };
      });
    },
    onPointerUp: (e: PointerEvent) => pointers.current.delete(e.pointerId),
    onPointerCancel: (e: PointerEvent) => pointers.current.delete(e.pointerId),
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
    setView({ w, cx: p.x, cy: p.y });
  };

  const viewBox = `${view.cx - view.w / 2} ${view.cy - (view.w * ratio) / 2} ${view.w} ${view.w * ratio}`;
  return { viewBox, svg, handlers, zoomAt, centerOn };
}

export type MapApi = { centerOn: (p: Point, w?: number) => void };

export type MapViewProps = {
  board: Board;
  players: Player[];
  className?: string;
  /** Players drawn by the caller (a moving token): left out of the stickers on cells */
  hide?: string[];
  /** The first view: the whole world, or a close look around this cell */
  focus?: number | undefined;
  /** Where the zoom buttons stand: at the bottom on a desktop, at the top over a phone's bottom sheet */
  tools?: 'top' | 'bottom' | 'none';
  children?: ReactNode;
  controls?: (api: MapApi) => void;
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
  controls,
}: MapViewProps) {
  const start = focus ? cellById(board, focus) : null;
  const { viewBox, svg, handlers, zoomAt, centerOn } = usePanZoom(
    board,
    start ? { w: 380, cx: start.x, cy: start.y } : null,
  );
  controls?.({ centerOn });
  const props = useMemo(() => scenery(board), [board]);
  const signs = useMemo(() => signSpots(board), [board]);
  const me = players.find((p) => p.me);
  // My cell is marked only while my sticker stands on it; a moving token is drawn by the caller
  const myCell = me && !hide.includes(me.id) ? me.cell : null;
  const finish = board.cells.find((c) => c.kind === 'finish');
  const onCell = (id: number) => players.filter((p) => p.cell === id && !hide.includes(p.id));

  return (
    <div className={cx('table-surface relative touch-none overflow-hidden select-none', className)}>
      <svg
        ref={svg}
        viewBox={viewBox}
        preserveAspectRatio="xMidYMid meet"
        role="img"
        tabIndex={0}
        aria-label={t.mapLabel(me?.cell ?? 0)}
        className="block size-full cursor-grab active:cursor-grabbing"
        onWheel={handlers.onWheel}
        onPointerDown={handlers.onPointerDown}
        onPointerMove={handlers.onPointerMove}
        onPointerUp={handlers.onPointerUp}
        onPointerCancel={handlers.onPointerCancel}
        onKeyDown={handlers.onKeyDown}
      >
        {/* No layer under the map: the table is the frame's own background (a huge SVG layer can exceed a phone
            GPU's texture limit and render in part). Zones carry their own outline: no black underlay either. */}
        {board.zones.map((z: BoardZone) => (
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

        {board.zones.map((z) => {
          const at = signs.get(z.id);
          if (!at) return null;
          const w = signWidth(z.name);
          return (
            <g key={`sign-${z.id}`} transform={`translate(${at.x} ${at.y})`}>
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

        {finish ? <Castle at={finish} /> : null}

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

        {board.cells.map((c) => {
          const big = c.kind === 'start' || c.kind === 'finish';
          const mine = myCell === c.id;
          const fill = mine
            ? 'var(--color-me)'
            : c.kind === 'event'
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
          return (
            <g key={c.id} data-cell={c.id}>
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
                  {c.kind === 'start' ? t.start : t.finish}
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

        {board.cells.flatMap((c) => {
          const here = onCell(c.id);
          // Me first, so my sticker is never the one hidden behind «+N»
          const ordered = [...here].sort((a, b) => Number(b.me ?? false) - Number(a.me ?? false));
          return [
            ...ordered
              .slice(0, 3)
              .map((p, i) => (
                <MapSticker key={p.id} player={p} at={c} index={i} count={here.length} />
              )),
            here.length > 3 ? (
              <MoreChip key={`more-${c.id}`} at={c} count={here.length - 3} />
            ) : null,
          ];
        })}

        {children}
      </svg>

      {tools === 'none' ? null : (
        <div className={cx('absolute right-3 grid gap-2', tools === 'top' ? 'top-3' : 'bottom-3')}>
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
