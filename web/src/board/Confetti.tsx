import { useReducedMotion } from 'motion/react';
import { useEffect, useEffectEvent, useRef } from 'react';
import { cx } from '../ui/cx';
import { momentBudget } from './moment';

const framesPerSecond = 60;
const burstFrames = 110;

/** A burst of confetti in the players' token colours: the finish only. Keyed by the parent per burst. */
export function Confetti({
  overlay = false,
  onDone,
}: { overlay?: boolean; onDone?: () => void } = {}) {
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const reduce = useReducedMotion() ?? false;
  const done = useEffectEvent(() => onDone?.());
  useEffect(() => {
    const c = canvas.current;
    const g = c?.getContext('2d');
    if (!c || !g) {
      const timer = window.setTimeout(done, 0);
      return () => {
        window.clearTimeout(timer);
      };
    }
    const style = getComputedStyle(document.documentElement);
    const colors = [1, 2, 3, 4, 6, 7].map((n) =>
      style.getPropertyValue(`--color-token-${n}`).trim(),
    );
    const k = devicePixelRatio;
    c.width = c.clientWidth * k;
    c.height = c.clientHeight * k;
    const bits = Array.from({ length: 140 }, () => ({
      x: c.width / 2,
      y: c.height * 0.4,
      vx: (Math.random() - 0.5) * 16 * k,
      vy: (-Math.random() * 14 - 4) * k,
      r: (4 + Math.random() * 5) * k,
      spin: Math.random() * 6,
      color: colors[Math.floor(Math.random() * colors.length)] ?? '',
    }));
    const started = performance.now();
    let id = 0;
    const stop = () => {
      cancelAnimationFrame(id);
      window.clearTimeout(timer);
      document.removeEventListener('click', finish, true);
      g.clearRect(0, 0, c.width, c.height);
    };
    const finish = () => {
      stop();
      done();
    };
    const tick = (now: number) => {
      const frame = ((now - started) * framesPerSecond) / 1000;
      if (frame >= burstFrames) {
        finish();
        return;
      }
      g.clearRect(0, 0, c.width, c.height);
      for (const b of bits) {
        g.save();
        g.translate(b.x + b.vx * frame, b.y + b.vy * frame + (0.45 * k * frame * (frame + 1)) / 2);
        g.rotate(b.spin + frame / 10);
        g.fillStyle = b.color;
        g.fillRect(-b.r, -b.r / 2, b.r * 2, b.r);
        g.restore();
      }
      id = requestAnimationFrame(tick);
    };
    const timer = window.setTimeout(finish, momentBudget * 1000);
    document.addEventListener('click', finish, true);
    id = requestAnimationFrame(tick);
    return stop;
  }, [reduce]);
  return reduce ? null : (
    <canvas
      ref={canvas}
      data-testid={overlay ? 'finish-confetti' : undefined}
      className={cx('pointer-events-none inset-0 size-full', overlay ? 'fixed z-10' : 'absolute')}
      aria-hidden
    />
  );
}
