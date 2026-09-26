import type { HTMLAttributes, ReactNode } from 'react';
import { cx } from './cx';

/** A flat panel: the leaderboard, the feed, a form. The hard shadow belongs to the main action and the moments. */
export function Panel({
  title,
  children,
  className,
  ...rest
}: HTMLAttributes<HTMLElement> & { title?: ReactNode }) {
  return (
    <section className={cx('grid gap-3 rounded-lg bg-card p-4', className)} {...rest}>
      {title ? <h2 className="font-display text-lg font-heavy">{title}</h2> : null}
      {children}
    </section>
  );
}

/** The card of a main moment's result: lifted off the table */
export function MomentCard({ children, className, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cx(
        'grid justify-items-center gap-2 rounded-lg border-3 border-ink bg-card p-4 text-center wrap-anywhere shadow-lift',
        className,
      )}
      {...rest}
    >
      {children}
    </div>
  );
}

/** The table: the felt-dotted wooden surface the map and the moments stand on */
export function Table({ children, className, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div className={cx('table-surface rounded-lg border-3 border-ink', className)} {...rest}>
      {children}
    </div>
  );
}
