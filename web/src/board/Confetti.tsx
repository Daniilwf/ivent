import { useReducedMotion } from 'motion/react';
import { useEffect, useEffectEvent, useRef } from 'react';
import { confettiMotion } from '../design/motion';
import { cx } from '../ui/cx';
import type { Point } from './types';

/** A burst of confetti in the players' token colours: the finish only. Keyed by the parent per burst. */
export function Confetti({
  overlay = false,
  origin,
  onDone,
}: {
  overlay?: boolean;
  /** The finish cell's point in viewport pixels, read when the canvas mounts. */
  origin?: () => Point | null;
  onDone?: () => void;
} = {}) {
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const reduce = useReducedMotion() ?? false;
  const done = useEffectEvent(() => onDone?.());
  const finishPoint = useEffectEvent((c: HTMLCanvasElement) => {
    if (!origin) return { x: c.clientWidth / 2, y: c.clientHeight * 0.4 };
    const point = origin();
    if (!point) return null;
    const bounds = c.getBoundingClientRect();
    return { x: point.x - bounds.left, y: point.y - bounds.top };
  });
  useEffect(() => {
    const c = canvas.current;
    const g = c?.getContext('2d');
    const point = c ? finishPoint(c) : null;
    if (!c || !g || !point) {
      const timer = window.setTimeout(done, 0);
      return () => {
        window.clearTimeout(timer);
      };
    }
    const style = getComputedStyle(document.documentElement);
    const colors = [1, 2, 3, 4, 6, 7].map((n) =>
      style.getPropertyValue(`--color-token-${n}`).trim(),
    );
    const k = window.devicePixelRatio;
    c.width = c.clientWidth * k;
    c.height = c.clientHeight * k;
    const bits = Array.from({ length: confettiMotion.count }, () => ({
      vx: (Math.random() - 0.5) * 160,
      vy: 25 + Math.random() * 85,
      r: (3 + Math.random() * 4) * k,
      spin: Math.random() * 6,
      color: colors[Math.floor(Math.random() * colors.length)] ?? '',
    }));
    const started = performance.now();
    let id = 0;
    let ended = false;
    const stop = () => {
      cancelAnimationFrame(id);
      window.clearTimeout(timer);
      document.removeEventListener('click', finish, true);
      g.clearRect(0, 0, c.width, c.height);
    };
    const finish = () => {
      if (ended) return;
      ended = true;
      stop();
      done();
    };
    const tick = (now: number) => {
      const seconds = Math.max(0, (now - started) / 1000);
      if (seconds >= confettiMotion.seconds) {
        finish();
        return;
      }
      g.clearRect(0, 0, c.width, c.height);
      for (const b of bits) {
        g.save();
        g.translate(
          (point.x + b.vx * seconds) * k,
          (point.y + b.vy * seconds + 50 * seconds ** 2) * k,
        );
        g.rotate(b.spin + seconds * 6);
        g.fillStyle = b.color;
        g.fillRect(-b.r, -b.r / 2, b.r * 2, b.r);
        g.restore();
      }
      id = requestAnimationFrame(tick);
    };
    const timer = window.setTimeout(finish, confettiMotion.seconds * 1000);
    document.addEventListener('click', finish, true);
    id = requestAnimationFrame(tick);
    return stop;
  }, [reduce]);
  return reduce ? null : (
    <canvas
      ref={canvas}
      data-testid={overlay ? 'finish-confetti' : undefined}
      className={cx('pointer-events-none inset-0 size-full', overlay ? 'fixed z-30' : 'absolute')}
      aria-hidden
    />
  );
}
