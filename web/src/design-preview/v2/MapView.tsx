import { Flag, Minus, Plus, LocateFixed, Sparkles } from 'lucide-react';
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent,
  type ReactNode,
  type WheelEvent,
} from 'react';
import { ru } from '../../i18n/ru';
import type { Player } from './content';
import { blob, cell, graph, world, zoneAt, zoneOf, zones, type Point, type ZoneId } from './graph';

const t = ru.designPreview;

export type MapVariant = 'world' | 'board';

// ---- Seeded randomness for the scenery: the same map every time ----
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

function nearRoute(p: Point, gap: number) {
  return graph.cells.some((c) => Math.hypot(c.x - p.x, c.y - p.y) < gap);
}

type Prop = Point & { zone: ZoneId; size: number; flip: boolean };

function scenery(): Prop[] {
  const random = seeded(7);
  const props: Prop[] = [];
  for (let i = 0; i < 2600 && props.length < 190; i++) {
    const p = { x: 30 + random() * (world.width - 60), y: 30 + random() * (world.height - 60) };
    if (nearRoute(p, 42) || props.some((q) => Math.hypot(q.x - p.x, q.y - p.y) < 30)) continue;
    const zone = zoneOf(p);
    if (!zone) continue;
    props.push({ ...p, zone, size: 0.8 + random() * 0.5, flip: random() > 0.5 });
  }
  return props;
}

function PropShape({ prop }: { prop: Prop }) {
  const { x, y, size: k } = prop;
  switch (prop.zone) {
    case 'forest':
      return (
        <g transform={`translate(${x} ${y}) scale(${k})`}>
          <rect x={-2.5} y={4} width={5} height={10} fill="var(--zone-line)" />
          <circle r={12} fill="var(--table-deep)" stroke="var(--zone-line)" strokeWidth={2.5} />
          <circle cx={-4} cy={-4} r={4} fill="var(--zone-meadow)" opacity={0.7} />
        </g>
      );
    case 'mountains':
      return (
        <g transform={`translate(${x} ${y}) scale(${k * (prop.flip ? -1 : 1)} ${k})`}>
          <path
            d="M-18 12 L-2 -16 L16 12 Z"
            fill="var(--zone-mountains)"
            stroke="var(--zone-line)"
            strokeWidth={2.5}
            strokeLinejoin="round"
          />
          <path d="M-8 -4 L-2 -16 L5 -4 L1 -1 L-3 -5 Z" fill="#ffffff" />
        </g>
      );
    case 'swamp':
      return (
        <g
          transform={`translate(${x} ${y}) scale(${k})`}
          stroke="var(--zone-line)"
          strokeWidth={2}
          fill="none"
          strokeLinecap="round"
        >
          <ellipse rx={13} ry={5} fill="var(--table-dots)" opacity={0.6} />
          <path d="M-6 0 V-14 M0 0 V-18 M6 0 V-12" />
          <ellipse cx={0} cy={-19} rx={2} ry={4} fill="var(--zone-line)" />
        </g>
      );
    case 'city':
      return (
        <g
          transform={`translate(${x} ${y}) scale(${k})`}
          stroke="var(--zone-line)"
          strokeWidth={2.2}
          strokeLinejoin="round"
        >
          <rect x={-10} y={-6} width={20} height={16} fill="#ffffff" />
          <path d="M-13 -6 L0 -18 L13 -6 Z" fill="var(--action)" />
          <rect x={-3} y={2} width={6} height={8} fill="var(--zone-line)" />
        </g>
      );
    case 'meadow':
      return (
        <g transform={`translate(${x} ${y}) scale(${k})`}>
          <circle r={5} fill="#ffffff" stroke="var(--zone-line)" strokeWidth={1.5} />
          <circle r={2} fill="var(--gold)" />
          <circle
            cx={11}
            cy={6}
            r={3}
            fill="var(--gold)"
            stroke="var(--zone-line)"
            strokeWidth={1.2}
          />
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
      stroke="var(--zone-line)"
      strokeWidth={3}
      strokeLinejoin="round"
    >
      <rect x={-22} y={-20} width={44} height={40} fill="#ffffff" />
      <rect x={-34} y={-38} width={16} height={58} fill="#ffffff" />
      <rect x={18} y={-38} width={16} height={58} fill="#ffffff" />
      <path d="M-34 -38 h4 v-6 h4 v6 h4 v-6 h4 v6" fill="none" />
      <path d="M18 -38 h4 v-6 h4 v6 h4 v-6 h4 v6" fill="none" />
      <path d="M-6 20 v-14 a6 6 0 0 1 12 0 v14" fill="var(--zone-line)" />
      <path d="M26 -38 V-62 L44 -55 L26 -48" fill="var(--gold)" />
    </g>
  );
}

// ---- Hex tiles of the board variant ----
function hexes() {
  const r = 34;
  const w = Math.sqrt(3) * r;
  const tiles: { x: number; y: number; zone: ZoneId; mark: boolean }[] = [];
  const random = seeded(11);
  for (let row = 0; row * r * 1.5 < world.height + r; row++) {
    for (let col = 0; col * w < world.width + w; col++) {
      const x = col * w + (row % 2 ? w / 2 : 0);
      const y = row * r * 1.5;
      tiles.push({
        x,
        y,
        zone: zoneAt({ x, y }),
        mark: !nearRoute({ x, y }, 46) && random() > 0.55,
      });
    }
  }
  const corners = Array.from({ length: 6 }, (_, i) => {
    const a = (Math.PI / 3) * i + Math.PI / 6;
    return `${(Math.cos(a) * (r - 1.5)).toFixed(1)},${(Math.sin(a) * (r - 1.5)).toFixed(1)}`;
  }).join(' ');
  return { tiles, corners };
}

const zoneFill: Record<ZoneId, string> = {
  meadow: 'var(--zone-meadow)',
  forest: 'var(--zone-forest)',
  mountains: 'var(--zone-mountains)',
  swamp: 'var(--zone-swamp)',
  city: 'var(--zone-city)',
  castle: 'var(--zone-castle)',
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
  const clip = `clip-${player.id}`;
  return (
    <g transform={`translate(${dx} ${dy}) rotate(${index % 2 ? 6 : -6})`}>
      <title>{player.name}</title>
      <circle r={r + 5} fill="var(--zone-line)" />
      <circle r={r + 3} fill="#ffffff" />
      <clipPath id={clip}>
        <circle r={r} />
      </clipPath>
      <circle r={r} fill={player.color} />
      {player.gif ? (
        <image
          href={player.gif}
          x={-r}
          y={-r}
          width={size}
          height={size}
          clipPath={`url(#${clip})`}
          preserveAspectRatio="xMidYMid slice"
        />
      ) : (
        <text className="cell-num" style={{ fill: player.ink, fontSize: size * 0.48 }}>
          {player.name[0]}
        </text>
      )}
      {player.me ? (
        <g>
          <circle r={r + 7} fill="none" stroke="var(--me)" strokeWidth={4} />
          <rect x={-16} y={-r - 24} width={32} height={18} rx={9} fill="var(--me)" />
          <text y={-r - 15} className="cell-num" style={{ fill: 'var(--me-ink)', fontSize: 12 }}>
            {t.you}
          </text>
        </g>
      ) : null}
    </g>
  );
}

/** «+N» over a cell with more players than fit */
function MoreChip({ at, count }: { at: Point; count: number }) {
  return (
    <g transform={`translate(${at.x + 30} ${at.y + 4})`}>
      <rect
        x={-17}
        y={-12}
        width={34}
        height={24}
        rx={12}
        fill="var(--card)"
        stroke="var(--zone-line)"
        strokeWidth={2.5}
      />
      <text className="cell-num" style={{ fontSize: 12 }}>
        {t.more(count)}
      </text>
    </g>
  );
}

// ---- Zone signs: placed where they cover no cell and stay inside their zone ----
const signFont = 16;
const signWidth = (text: string) => text.length * signFont * 0.72 + 28;

function signSpots(): Record<ZoneId, Point> {
  const spots = {} as Record<ZoneId, Point>;
  for (const z of zones) {
    const half = signWidth(t.zones[z.id]) / 2;
    let best: { p: Point; score: number } | null = null;
    for (let x = 30 + half; x < world.width - 30 - half; x += 10) {
      for (let y = 40; y < world.height - 40; y += 10) {
        // The centre inside the zone, the sign's corners mostly too: a sign may hang over the coast a little
        if (zoneOf({ x, y }) !== z.id) continue;
        const edges = [-half, 0, half].flatMap((dx) =>
          [-15, 15].map((dy) => ({ x: x + dx, y: y + dy })),
        );
        if (edges.filter((e) => zoneOf(e) !== z.id).length > 2) continue;
        // Distance from the sign's box to the nearest cell: the larger, the clearer
        const clear = Math.min(
          ...graph.cells.map((c) =>
            Math.hypot(Math.max(Math.abs(c.x - x) - half, 0), Math.max(Math.abs(c.y - y) - 16, 0)),
          ),
        );
        if (!best || clear > best.score) best = { p: { x, y }, score: clear };
        if (clear > 60) break;
      }
    }
    spots[z.id] = best?.p ?? z.label;
  }
  return spots;
}

// ---- Pan and zoom ----
/** The view is its centre and its width in world units; the height follows the frame's shape */
type View = { cx: number; cy: number; w: number };
type Box = { width: number; height: number };

const fitWorld = (box: Box): View => {
  const ratio = box.height / box.width;
  return {
    cx: world.width / 2,
    cy: world.height / 2,
    w: Math.max(world.width, world.height / ratio) * 1.04,
  };
};

function usePanZoom(initial: View | null) {
  // null until the user moves the map: the whole world, fitted to the frame
  const [moved, setView] = useState<View | null>(initial);
  const [box, setBox] = useState<Box>({ width: world.width, height: world.height });
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

  const view = moved ?? fitWorld(box);
  const ratio = box.height / box.width;
  const current = (v: View | null) => v ?? fitWorld(box);

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
      const w = Math.min(Math.max(v.w * factor, 260), fitWorld(box).w * 1.2);
      const focus =
        clientX !== undefined && clientY !== undefined
          ? toWorld(clientX, clientY, v)
          : { x: v.cx, y: v.cy };
      const k = w / v.w;
      return { w, cx: focus.x - (focus.x - v.cx) * k, cy: focus.y - (focus.y - v.cy) * k };
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
        const [a, b] = [...pointers.current.values()];
        const before = Math.hypot((a as Point).x - (b as Point).x, (a as Point).y - (b as Point).y);
        pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
        const [c, d] = [...pointers.current.values()];
        const after = Math.hypot((c as Point).x - (d as Point).x, (c as Point).y - (d as Point).y);
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
  };

  const centerOn = (p: Point, w = view.w) => {
    setView({ w, cx: p.x, cy: p.y });
  };

  const viewBox = `${view.cx - view.w / 2} ${view.cy - (view.w * ratio) / 2} ${view.w} ${view.w * ratio}`;
  return { viewBox, svg, handlers, zoomAt, centerOn };
}

export type MapProps = {
  variant: MapVariant;
  players: Player[];
  className?: string;
  /** Players drawn by the caller (a moving token): left out of the stickers on cells */
  hide?: string[];
  /** Initial view: the whole world, or a close look around a cell */
  focus?: number | undefined;
  children?: ReactNode;
  controls?: (api: { centerOn: (p: Point, w?: number) => void }) => void;
};

export function MapView({
  variant,
  players,
  className,
  hide = [],
  focus,
  children,
  controls,
}: MapProps) {
  const start = focus ? cell(focus) : null;
  const { viewBox, svg, handlers, zoomAt, centerOn } = usePanZoom(
    start ? { w: 380, cx: start.x, cy: start.y } : null,
  );
  controls?.({ centerOn });
  const props = useMemo(() => scenery(), []);
  const board = useMemo(() => hexes(), []);
  const signs = useMemo(() => signSpots(), []);
  const me = players.find((p) => p.me);
  // My cell is marked only while my sticker stands on it; a moving token is drawn by the caller
  const myCell = me && !hide.includes(me.id) ? me.cell : null;
  const finish = graph.cells.find((c) => c.kind === 'finish') as Point;
  const onCell = (id: number) => players.filter((p) => p.cell === id && !hide.includes(p.id));

  return (
    <div className={`map-frame ${className ?? ''}`}>
      <svg
        ref={svg}
        viewBox={viewBox}
        preserveAspectRatio="xMidYMid meet"
        role="img"
        aria-label={t.mapLabel(me?.cell ?? 0)}
        onWheel={handlers.onWheel}
        onPointerDown={handlers.onPointerDown}
        onPointerMove={handlers.onPointerMove}
        onPointerUp={handlers.onPointerUp}
        onPointerCancel={handlers.onPointerCancel}
      >
        <defs>
          <pattern id="felt" width="14" height="14" patternUnits="userSpaceOnUse">
            <circle cx="7" cy="7" r="1.6" fill="var(--table-dots)" />
          </pattern>
        </defs>
        <rect x={-2000} y={-2000} width={6000} height={6000} fill="var(--table)" />
        <rect x={-2000} y={-2000} width={6000} height={6000} fill="url(#felt)" />

        {variant === 'world' ? (
          <g>
            {/* The coast: every zone's outline drawn thick first, the fills over it, so only the outer edge shows */}
            {zones.map((z) => (
              <path
                key={`edge-${z.id}`}
                d={blob(z.points)}
                fill="var(--zone-line)"
                stroke="var(--zone-line)"
                strokeWidth={14}
                strokeLinejoin="round"
              />
            ))}
            {zones.map((z) => (
              <path
                key={z.id}
                d={blob(z.points)}
                fill={zoneFill[z.id]}
                stroke={zoneFill[z.id]}
                strokeWidth={2}
              />
            ))}
            {props.map((p, i) => (
              <PropShape key={i} prop={p} />
            ))}
          </g>
        ) : (
          <g>
            <rect
              x={-18}
              y={-18}
              width={world.width + 36}
              height={world.height + 36}
              rx={28}
              fill="var(--zone-line)"
              transform="translate(8 10)"
            />
            <rect
              x={-18}
              y={-18}
              width={world.width + 36}
              height={world.height + 36}
              rx={28}
              fill="var(--card)"
              stroke="var(--zone-line)"
              strokeWidth={4}
            />
            <clipPath id="board-clip">
              <rect x={0} y={0} width={world.width} height={world.height} rx={14} />
            </clipPath>
            <g clipPath="url(#board-clip)">
              {board.tiles.map((tile, i) => (
                <g key={i} transform={`translate(${tile.x} ${tile.y})`}>
                  <polygon
                    points={board.corners}
                    fill={zoneFill[tile.zone]}
                    stroke="var(--zone-line)"
                    strokeWidth={2}
                    strokeOpacity={0.35}
                  />
                  {tile.mark ? (
                    <PropShape
                      prop={{ x: 0, y: 0, zone: tile.zone, size: 0.7, flip: i % 2 === 0 }}
                    />
                  ) : null}
                </g>
              ))}
            </g>
            <rect
              x={0}
              y={0}
              width={world.width}
              height={world.height}
              rx={14}
              fill="none"
              stroke="var(--zone-line)"
              strokeWidth={4}
            />
          </g>
        )}

        {zones.map((z) => {
          const at = signs[z.id];
          const w = signWidth(t.zones[z.id]);
          return (
            <g key={`sign-${z.id}`} transform={`translate(${at.x} ${at.y})`}>
              <rect
                x={-w / 2}
                y={-15}
                width={w}
                height={30}
                rx={15}
                fill="var(--zone-line)"
                transform="translate(0 3)"
              />
              <rect
                x={-w / 2}
                y={-15}
                width={w}
                height={30}
                rx={15}
                fill="var(--card)"
                stroke="var(--zone-line)"
                strokeWidth={2.5}
              />
              <text className="zone-label" textAnchor="middle" dominantBaseline="central">
                {t.zones[z.id]}
              </text>
            </g>
          );
        })}

        <Castle at={finish} />

        {/* The route: a road with an edge, then the cells over it */}
        {graph.curves.map((c, i) => (
          <path
            key={`r1-${i}`}
            d={c.d}
            fill="none"
            stroke="var(--route-edge)"
            strokeWidth={variant === 'world' ? 22 : 18}
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        ))}
        {graph.curves.map((c, i) => (
          <path
            key={`r2-${i}`}
            d={c.d}
            fill="none"
            stroke="var(--route)"
            strokeWidth={variant === 'world' ? 15 : 11}
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeDasharray={variant === 'world' ? undefined : '1 0'}
          />
        ))}

        {graph.cells.map((c) => {
          const big = c.kind === 'start' || c.kind === 'finish';
          const mine = myCell === c.id;
          const fill = mine
            ? 'var(--me)'
            : c.kind === 'event'
              ? 'var(--gold)'
              : c.kind === 'checkpoint'
                ? 'var(--zone-meadow)'
                : big
                  ? 'var(--zone-line)'
                  : 'var(--card)';
          return (
            <g key={c.id}>
              {c.kind === 'fork' ? (
                <rect
                  x={c.x - 17}
                  y={c.y - 17}
                  width={34}
                  height={34}
                  rx={6}
                  transform={`rotate(45 ${c.x} ${c.y})`}
                  fill="var(--card)"
                  stroke="var(--zone-line)"
                  strokeWidth={3}
                />
              ) : (
                <circle
                  cx={c.x}
                  cy={c.y}
                  r={big ? 30 : 18}
                  fill={fill}
                  stroke="var(--zone-line)"
                  strokeWidth={3}
                />
              )}
              {big ? (
                <text
                  x={c.x}
                  y={c.y}
                  className="cell-num"
                  style={{ fill: '#ffffff', fontSize: 12 }}
                >
                  {c.kind === 'start' ? t.startCell : t.finishCell}
                </text>
              ) : c.kind === 'event' ? (
                <Sparkles
                  x={c.x - 10}
                  y={c.y - 10}
                  width={20}
                  height={20}
                  color="var(--zone-line)"
                  strokeWidth={2.5}
                  aria-hidden
                />
              ) : c.kind === 'checkpoint' ? (
                <Flag
                  x={c.x - 10}
                  y={c.y - 10}
                  width={20}
                  height={20}
                  color="var(--zone-line)"
                  strokeWidth={2.5}
                  aria-hidden
                />
              ) : (
                <text
                  x={c.x}
                  y={c.y}
                  className="cell-num"
                  style={mine ? { fill: 'var(--me-ink)' } : undefined}
                >
                  {c.id}
                </text>
              )}
            </g>
          );
        })}

        {graph.cells.flatMap((c) => {
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

      <div className="map-tools">
        <button
          type="button"
          className="btn-icon"
          aria-label={t.zoomIn}
          onClick={() => {
            zoomAt(0.8);
          }}
        >
          <Plus size={20} />
        </button>
        <button
          type="button"
          className="btn-icon"
          aria-label={t.zoomOut}
          onClick={() => {
            zoomAt(1.25);
          }}
        >
          <Minus size={20} />
        </button>
        {me ? (
          <button
            type="button"
            className="btn-icon"
            aria-label={t.toMe}
            onClick={() => {
              centerOn(cell(me.cell), 380);
            }}
          >
            <LocateFixed size={20} />
          </button>
        ) : null}
      </div>
    </div>
  );
}
