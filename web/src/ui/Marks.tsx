import type { ReactNode } from 'react';
import { cx } from './cx';

const chipTones = {
  muted: 'bg-muted',
  success: 'bg-success-soft',
  info: 'bg-info-soft',
  warning: 'bg-warning-soft',
} as const;

/**
 * A small fact in a pill: the deadline, a status. No ink outline: only what can be pressed has one. A tone tints the
 * pill for a status; the words (and an icon) carry the meaning, the colour only helps
 */
export function Chip({
  icon,
  tone = 'muted',
  children,
}: {
  icon?: ReactNode;
  tone?: keyof typeof chipTones;
  children: ReactNode;
}) {
  return (
    <span
      className={cx(
        'inline-flex max-w-full items-center gap-2 rounded-full px-3 py-1 text-sm font-medium text-ink',
        chipTones[tone],
      )}
    >
      {icon}
      {children}
    </span>
  );
}

const badgeTones = {
  gold: 'bg-gold text-ink',
  ink: 'bg-ink text-on-color tabular-nums',
  me: 'bg-me text-on-color',
  muted: 'bg-muted text-ink',
  warning: 'bg-warning-soft text-ink',
  danger: 'bg-danger-soft text-ink',
} as const;

/**
 * A mark next to a name: «Первый» in gold, «ты» in my colour, an account's role, «удалена», «ролл закрыт» (the admin's
 * pages). The words (and an icon) carry the meaning, the colour only helps
 */
export function Badge({
  tone = 'gold',
  icon,
  children,
  label,
  'data-testid': testId,
}: {
  tone?: keyof typeof badgeTones;
  icon?: ReactNode;
  children: ReactNode;
  /** What a screen reader says instead of the visible text: «Оценка: 7 из 10» for «★ 7 из 10» */
  label?: string;
  'data-testid'?: string;
}) {
  return (
    <span
      data-testid={testId}
      role={label ? 'img' : undefined}
      aria-label={label}
      className={cx(
        'inline-flex shrink-0 items-center gap-1 rounded-full px-2 text-xs font-bold',
        badgeTones[tone],
      )}
    >
      {icon}
      {children}
    </span>
  );
}

/**
 * How many things wait for me (D-263): proofs to check, later votes or challenges. A to-do, not an alarm — the action
 * fill, never danger, which is kept for drop, undo, reject and delete. 99+ at most; hidden from screen readers, so the
 * control it sits on says the number in words. Nothing for zero.
 */
export function CountBadge({
  count,
  className,
  'data-testid': testId,
}: {
  count: number;
  className?: string;
  'data-testid'?: string;
}) {
  if (count <= 0) return null;
  return (
    <span
      data-testid={testId}
      aria-hidden
      className={cx(
        'inline-flex h-5 min-w-5 items-center justify-center rounded-full bg-action px-1 text-xs font-bold text-on-color tabular-nums',
        className,
      )}
    >
      {count > 99 ? '99+' : String(count)}
    </span>
  );
}

/** A game's genre or feature */
export function Tag({ children }: { children: ReactNode }) {
  return <span className="rounded-full bg-muted px-2 text-xs">{children}</span>;
}
