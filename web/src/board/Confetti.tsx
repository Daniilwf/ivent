import { useReducedMotion } from 'motion/react';
import { useEffect, useRef } from 'react';

/** A burst of confetti in the players' token colours: the finish only. Keyed by the parent per burst. */
export function Confetti() {
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const reduce = useReducedMotion() ?? false;
  useEffect(() => {
    const c = canvas.current;
    const g = c?.getContext('2d');
    if (!c || !g || reduce) return;
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
    let frame = 0;
    let id = 0;
    const tick = () => {
      g.clearRect(0, 0, c.width, c.height);
      for (const b of bits) {
        b.vy += 0.45 * k;
        b.x += b.vx;
        b.y += b.vy;
        g.save();
        g.translate(b.x, b.y);
        g.rotate(b.spin + frame / 10);
        g.fillStyle = b.color;
        g.fillRect(-b.r, -b.r / 2, b.r * 2, b.r);
        g.restore();
      }
      if (++frame < 110) id = requestAnimationFrame(tick);
      else g.clearRect(0, 0, c.width, c.height);
    };
    id = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(id);
    };
  }, [reduce]);
  return (
    <canvas ref={canvas} className="pointer-events-none absolute inset-0 size-full" aria-hidden />
  );
}
