import { animate, useReducedMotion } from 'motion/react';
import {
  useEffect,
  useEffectEvent,
  useImperativeHandle,
  useRef,
  useState,
  type ReactNode,
  type Ref,
} from 'react';
import { cellById } from './geometry';
import { MapSticker, MapView, type MapApi } from './MapView';
import type { MomentHandle, MomentPhase } from './moment';
import type { Board, Player, Point } from './types';

/** A token hopping along its path cell by cell, the camera following each hop. Give it `key` per move. */
export function TokenMove({
  board,
  players,
  mover,
  path,
  className,
  size = 36,
  onPhase,
  ref,
  children,
}: {
  board: Board;
  players: Player[];
  /** The moving player, drawn on the path instead of on their cell */
  mover: Player;
  /** The cells the token passes, its current cell first; null while it stands */
  path: number[] | null;
  className?: string;
  size?: number;
  onPhase?: ((phase: MomentPhase) => void) | undefined;
  ref?: Ref<MomentHandle> | undefined;
  children?: ReactNode;
}) {
  const reduce = useReducedMotion() ?? false;
  const last = path ? (path.at(-1) as number) : mover.cell;
  const [pos, setPos] = useState<Point>(() =>
    cellById(board, reduce ? last : (path?.[0] ?? mover.cell)),
  );
  const camera = useRef<MapApi | null>(null);
  const controls = useRef<ReturnType<typeof animate> | null>(null);
  const stopped = useRef(false);

  const step = useEffectEvent((p: Point) => {
    setPos(p);
  });
  const arrive = useEffectEvent(() => {
    const end = cellById(board, last);
    setPos(end);
    camera.current?.centerOn(end, 420);
    onPhase?.('done');
  });

  const finish = () => {
    if (stopped.current) return;
    stopped.current = true;
    controls.current?.stop();
    const end = cellById(board, last);
    setPos(end);
    camera.current?.centerOn(end, 420);
    onPhase?.('done');
  };
  useImperativeHandle(ref, () => ({ skip: finish }));

  useEffect(() => {
    if (!path) return;
    if (reduce) {
      // The token already stands on its last cell; the camera and the report follow at once
      const id = window.setTimeout(arrive, 0);
      return () => {
        window.clearTimeout(id);
      };
    }
    stopped.current = false;
    const halted = () => stopped.current;
    const run = async () => {
      for (let i = 1; i < path.length; i++) {
        const a = cellById(board, path[i - 1] as number);
        const b = cellById(board, path[i] as number);
        // The camera moves once a hop, not every frame
        camera.current?.centerOn(b, 420);
        controls.current = animate(0, 1, {
          duration: 0.22,
          ease: 'easeInOut',
          onUpdate: (k) => {
            step({
              x: a.x + (b.x - a.x) * k,
              y: a.y + (b.y - a.y) * k - Math.sin(Math.PI * k) * 22,
            });
          },
        });
        await controls.current;
        if (halted()) return;
      }
      stopped.current = true;
      arrive();
    };
    void run();
    return () => {
      stopped.current = true;
      controls.current?.stop();
    };
  }, [board, path, reduce]);

  return (
    <MapView
      board={board}
      players={players}
      hide={[mover.id]}
      focus={path?.[0] ?? mover.cell}
      tools="none"
      {...(className ? { className } : {})}
      controls={(api) => {
        camera.current = api;
      }}
    >
      <MapSticker player={mover} at={pos} size={size} />
      {children}
    </MapView>
  );
}
