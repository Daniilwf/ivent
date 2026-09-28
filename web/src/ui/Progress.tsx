import { Flag } from 'lucide-react';
import { ru } from '../i18n/ru';
import { cx } from './cx';

/** How far along the route: cells left against the whole way from start to finish, in my colour (direction B's
 *  bar). Counted in cells along the way, not by cell numbers: on a board with forks the numbers are not in order. */
export function RouteProgress({ left, total }: { left: number; total: number }) {
  const done = Math.min(Math.max(total - left, 0), total);
  const share = total > 0 ? done / total : 0;
  return (
    <div className="grid gap-2">
      <span className="flex items-center justify-between text-sm font-medium">
        <span>{ru.ui.toFinish(left)}</span>
        <Flag size={16} aria-hidden />
      </span>
      <span
        role="progressbar"
        aria-label={ru.ui.routeProgress}
        aria-valuemin={0}
        aria-valuemax={total}
        aria-valuenow={done}
        className="block h-3 overflow-hidden rounded-full border-2 border-ink bg-muted"
      >
        <span
          className="block h-full border-r-2 border-ink bg-me transition-all duration-(--duration-slow)"
          style={{ width: `${share * 100}%` }}
        />
      </span>
    </div>
  );
}

/** Points against the leader's: a thin bar under a leaderboard row, in the player's token colour */
export function ScoreBar({ points, top, fill }: { points: number; top: number; fill: string }) {
  const share = top > 0 ? Math.min(Math.max(points / top, 0), 1) : 0;
  return (
    <span className="mt-1 block h-2 overflow-hidden rounded-full bg-muted" aria-hidden>
      <span
        className="block h-full rounded-full transition-all duration-(--duration-slow)"
        style={{ width: `${share * 100}%`, background: fill }}
      />
    </span>
  );
}

/** A grey stand-in while data loads: the same size as what comes, so nothing jumps */
export function Skeleton({ className }: { className?: string }) {
  return (
    <span
      className={cx(
        'block animate-pulse bg-muted motion-reduce:animate-none',
        // The page's own rounding wins: two rounded-* classes are decided by the CSS order, and rounded-sm comes last
        className?.includes('rounded-') ? null : 'rounded-sm',
        className,
      )}
      aria-hidden
    />
  );
}
