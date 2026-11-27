import { useReducedMotion, type animate } from 'motion/react';
import {
  useEffect,
  useEffectEvent,
  useImperativeHandle,
  useRef,
  useState,
  type ReactNode,
  type Ref,
} from 'react';
import { cameraFrameSeconds, moveMotion, movePathSeconds } from '../design/motion';
import { cellById } from './geometry';
import { loadMotion } from './loadMotion';
import { MapSticker, MapView, type MapApi } from './MapView';
import { momentBudget, type MomentHandle, type MomentPhase } from './moment';
import type { Board, Player, Point } from './types';

/** The start and the finish are big cells: a sticker on them stands higher, clear of the label */
const atBigCell = (board: Board, p: Point) =>
  board.cells.some(
    (c) => (c.kind === 'start' || c.kind === 'finish') && Math.hypot(c.x - p.x, c.y - p.y) < 1,
  );

/** No flights: one array for every render, so the move does not restart */
const noJumps: number[] = [];

/** A token hopping along its path after the camera frames it. Give it `key` per move. */
export function TokenMove({
  board,
  players,
  mover,
  path,
  className,
  size = 36,
  onArrive,
  onPhase,
  ref,
  children,
  budget = momentBudget,
  jumps = noJumps,
}: {
  /** Hops that are transfers, not steps (a teleport): by the index of the cell they land on; the token flies there */
  jumps?: number[];
  /** Seconds the whole move takes at most (a part of a longer moment gets less) */
  budget?: number;
  board: Board;
  players: Player[];
  /** The moving player, drawn on the path instead of on their cell */
  mover: Player;
  /** The cells the token passes, its current cell first; null while it stands */
  path: number[] | null;
  className?: string;
  size?: number;
  /** Destination in viewport pixels after the animated move, while its map is still mounted. */
  onArrive?: ((position: Point | null) => void) | undefined;
  onPhase?: ((phase: MomentPhase) => void) | undefined;
  ref?: Ref<MomentHandle> | undefined;
  children?: ReactNode;
}) {
  const reduce = useReducedMotion() ?? false;
  // One move per key. A live refresh can supply new objects for the same path.
  const [move] = useState(() => ({ board, path, jumps, budget }));
  const last = move.path?.at(-1) ?? mover.cell;
  const [pos, setPos] = useState<Point>(() =>
    cellById(move.board, reduce ? last : (move.path?.[0] ?? mover.cell)),
  );
  const camera = useRef<MapApi | null>(null);
  const controls = useRef<ReturnType<typeof animate> | null>(null);
  const deadline = useRef<number | undefined>(undefined);
  // The end is reported once, whether the token arrives or the moment is skipped
  const ended = useRef(false);

  const step = useEffectEvent((p: Point) => {
    setPos(p);
  });
  const end = (walked: boolean) => {
    if (ended.current) return;
    ended.current = true;
    window.clearTimeout(deadline.current);
    controls.current?.stop();
    controls.current = null;
    const cell = cellById(move.board, last);
    setPos(cell);
    if (walked) onArrive?.(camera.current?.project(cell) ?? null);
    onPhase?.('done');
  };
  const arrive = useEffectEvent(end);

  // The map's token is about to move: the animation engine comes right after the page, not when the move starts
  useEffect(() => {
    loadMotion().catch(() => undefined);
  }, []);

  useImperativeHandle(ref, () => ({
    skip: () => {
      end(false);
    },
  }));

  useEffect(() => {
    const { board, path, budget, jumps } = move;
    if (!path || ended.current) return;

    // Loading Motion is part of the budget, including when this is the walking part of a completion.
    const timer = window.setTimeout(
      () => {
        arrive(false);
      },
      reduce ? 0 : budget * 1000,
    );
    deadline.current = timer;
    let cancelled = false;
    const halted = () => cancelled || ended.current;
    const run = async () => {
      // Without the animation engine (its download failed) the token stands on its last cell at once
      const motion = await loadMotion().catch(() => null);
      if (halted()) return;
      if (!motion) {
        arrive(false);
        return;
      }
      const { animate } = motion;
      const points = path.map((id) => cellById(board, id));
      const plan = camera.current?.planMove(points);
      const frameSeconds = plan?.frame
        ? Math.min(cameraFrameSeconds, Math.max(0, budget - moveMotion.minHopSeconds))
        : 0;
      if (plan?.frame && frameSeconds > 0) {
        controls.current = animate(0, 1, {
          duration: frameSeconds,
          ease: 'easeInOut',
          onUpdate: plan.frame,
        });
        await controls.current;
        if (halted()) return;
      }

      const travelBudget = Math.min(movePathSeconds, budget - frameSeconds);
      const steps = points.length - 1;
      const units = steps + jumps.filter((i) => i > 0 && i <= steps).length;
      const long = units * moveMotion.minHopSeconds > travelBudget;
      const groups = long
        ? Math.min(steps, Math.max(1, Math.floor(travelBudget / moveMotion.minHopSeconds)))
        : steps;
      const hop = Math.min(
        moveMotion.hopSeconds,
        travelBudget / Math.max(1, long ? groups : units),
      );
      const started = performance.now();
      for (let group = 1; group <= groups; group++) {
        // A long route groups several cells into a single hop, still crossing them in order.
        const start = Math.round(((group - 1) * steps) / groups);
        const finish = Math.round((group * steps) / groups);
        const from = points[start] as Point;
        const to = points[finish] as Point;
        const flight = finish === start + 1 && jumps.includes(finish);
        const height = flight
          ? Math.max(Math.hypot(to.x - from.x, to.y - from.y) / 3, 40)
          : moveMotion.hopHeight;
        controls.current = animate(0, 1, {
          duration: flight && !long ? hop * 2 : hop,
          ease: 'easeInOut',
          onUpdate: (k) => {
            if (halted()) return;
            const progress = start + (finish - start) * k;
            const index = Math.min(finish - 1, Math.floor(progress));
            const a = points[index] as Point;
            const b = points[index + 1] as Point;
            const along = progress - index;
            const position = { x: a.x + (b.x - a.x) * along, y: a.y + (b.y - a.y) * along };
            plan?.follow?.(position, (performance.now() - started) / 1000);
            step({ ...position, y: position.y - Math.sin(Math.PI * k) * height });
          },
        });
        await controls.current;
        if (halted()) return;
      }
      arrive(true);
    };
    if (!reduce) void run();
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
      controls.current?.stop();
      controls.current = null;
    };
  }, [move, reduce]);

  return (
    <MapView
      board={move.board}
      players={players}
      hide={[mover.id]}
      focus={move.path?.[0] ?? mover.cell}
      tools="none"
      {...(className ? { className } : {})}
      ref={camera}
    >
      <MapSticker player={mover} at={pos} size={size} lift={atBigCell(move.board, pos) ? 52 : 32} />
      {children}
    </MapView>
  );
}
