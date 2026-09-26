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

/** A mark next to a name: «Первый» in gold, «ты» in my colour */
export function Badge({ tone = 'gold', children }: { tone?: 'gold' | 'me'; children: ReactNode }) {
  return (
    <span
      className={cx(
        'inline-flex shrink-0 items-center rounded-full px-2 text-xs font-bold',
        tone === 'gold' ? 'bg-gold text-ink' : 'bg-me text-on-color',
      )}
    >
      {children}
    </span>
  );
}

/** A game's genre or feature */
export function Tag({ children }: { children: ReactNode }) {
  return <span className="rounded-full bg-muted px-2 text-xs">{children}</span>;
}
