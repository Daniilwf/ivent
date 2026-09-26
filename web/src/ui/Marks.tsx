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
  'data-testid': testId,
}: {
  tone?: keyof typeof badgeTones;
  icon?: ReactNode;
  children: ReactNode;
  'data-testid'?: string;
}) {
  return (
    <span
      data-testid={testId}
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

/** A game's genre or feature */
export function Tag({ children }: { children: ReactNode }) {
  return <span className="rounded-full bg-muted px-2 text-xs">{children}</span>;
}
