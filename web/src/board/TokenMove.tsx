import { easeInOut, useReducedMotion, type animate } from 'motion/react';
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
import { hopPoint, moveHops, stickerLift } from './camera';
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
      const radius = size / 2;
      const circle = radius + (mover.me ? 9 : 5);
      // Account for the outer ring and the tilted name badge above the moving sticker.
      const padX = Math.max(moveMotion.pathPadding, circle);
      const sticker = {
        x0: -padX,
        x1: padX,
        y0: -stickerLift.onBigCell - (mover.me ? radius + 26 : circle),
        y1: Math.max(moveMotion.pathPadding, circle - stickerLift.onCell),
      };
      const frameSeconds = Math.min(
        cameraFrameSeconds,
        Math.max(0, budget - moveMotion.minHopSeconds),
      );
      let hops = moveHops(points, jumps, Math.min(movePathSeconds, budget));
      let plan = camera.current?.planMove(points, { hops, sticker });
      if (plan?.frame && frameSeconds > 0) {
        hops = moveHops(points, jumps, Math.min(movePathSeconds, budget - frameSeconds));
        plan = camera.current?.planMove(points, { hops, sticker });
        if (plan?.frame) {
          controls.current = animate(0, 1, {
            duration: frameSeconds,
            ease: 'easeInOut',
            onUpdate: plan.frame,
          });
          await controls.current;
          if (halted()) return;
        }
      } else {
        plan?.frame?.(1);
      }

      let elapsed = 0;
      for (const hop of hops) {
        const { duration, height } = hop;
        controls.current = animate(0, 1, {
          duration,
          ease: 'linear',
          onUpdate: (time) => {
            if (halted()) return;
            // The token eases into and out of its hop. The camera travels at a steady pace.
            const k = easeInOut(time);
            const position = hopPoint(points, hop, k);
            plan?.follow?.(elapsed + time * duration);
            step({ ...position, y: position.y - Math.sin(Math.PI * k) * height });
          },
        });
        await controls.current;
        if (halted()) return;
        elapsed += duration;
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
  }, [move, mover.me, reduce, size]);

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
      <MapSticker
        player={mover}
        at={pos}
        size={size}
        lift={atBigCell(move.board, pos) ? stickerLift.onBigCell : stickerLift.onCell}
      />
      {children}
    </MapView>
  );
}
