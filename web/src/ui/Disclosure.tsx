import { ChevronRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { cx } from './cx';

/**
 * The line that opens a folded part (`<details>`): a lucide chevron that turns when it is open, instead of the
 * browser's own triangle, which looked different in every engine and was the one icon outside lucide (K2-5). The
 * browser's marker is hidden for every summary in tokens.css.
 */
export function Summary({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <summary
      className={cx(
        'flex min-h-11 cursor-pointer items-center gap-2 rounded-md is-focus:focus-ring',
        className,
      )}
    >
      <ChevronRight
        size={20}
        aria-hidden
        className="shrink-0 transition-transform duration-(--duration-fast) ease-out in-open:rotate-90"
      />
      <span className="min-w-0">{children}</span>
    </summary>
  );
}
