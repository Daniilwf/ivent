import { ChevronRight, LoaderCircle } from 'lucide-react';
import type { ButtonHTMLAttributes, ReactNode, Ref } from 'react';
import { cx } from './cx';

const line = 'flex min-h-11 cursor-pointer items-center gap-2 rounded-md is-focus:focus-ring';
const chevron = 'shrink-0 transition-transform duration-(--duration-fast) ease-out';

/**
 * The line that opens a folded part (`<details>`): a lucide chevron that turns when it is open, instead of the
 * browser's own triangle, which looked different in every engine and was the one icon outside lucide (K2-5). The
 * browser's marker is hidden for every summary in tokens.css.
 */
export function Summary({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <summary className={cx(line, className)}>
      <ChevronRight size={20} aria-hidden className={cx(chevron, 'in-open:rotate-90')} />
      <span className="min-w-0">{children}</span>
    </summary>
  );
}

/** A disclosure that opens asynchronously and owns its expanded state. */
export function SummaryButton({
  open,
  busy = false,
  className,
  children,
  ...button
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  open: boolean;
  busy?: boolean;
  ref?: Ref<HTMLButtonElement>;
}) {
  return (
    <button
      {...button}
      type="button"
      aria-expanded={open}
      aria-busy={busy || button['aria-busy']}
      className={cx(line, className)}
    >
      {busy ? (
        <LoaderCircle
          size={20}
          aria-hidden
          className="shrink-0 animate-spin motion-reduce:animate-none"
        />
      ) : (
        <ChevronRight size={20} aria-hidden className={cx(chevron, open && 'rotate-90')} />
      )}
      <span className="min-w-0">{children}</span>
    </button>
  );
}
