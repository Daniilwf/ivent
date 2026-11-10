import { CircleAlert, CircleCheck, Info, TriangleAlert, WifiOff } from 'lucide-react';
import type { ReactNode } from 'react';
import { ru } from '../i18n/ru';
import { Button } from './Button';
import { cx } from './cx';

export type Tone = 'success' | 'info' | 'warning' | 'danger';

const tones: Record<Tone, { box: string; icon: ReactNode }> = {
  success: {
    box: cx('border-success bg-success-soft text-success'),
    icon: <CircleCheck size={20} aria-hidden />,
  },
  info: { box: cx('border-info bg-info-soft text-info'), icon: <Info size={20} aria-hidden /> },
  warning: {
    box: cx('border-warning bg-warning-soft text-warning'),
    icon: <TriangleAlert size={20} aria-hidden />,
  },
  danger: {
    box: cx('border-danger bg-danger-soft text-danger'),
    icon: <CircleAlert size={20} aria-hidden />,
  },
};

/** A line of news about what just happened: colour, an icon and words together, never colour alone */
export function Notice({ tone, children }: { tone: Tone; children: ReactNode }) {
  return (
    <p
      role={tone === 'danger' ? 'alert' : 'status'}
      className={cx(
        'flex items-start gap-2 rounded-md px-3 py-2 text-sm font-medium',
        tones[tone].box,
      )}
    >
      <span className="shrink-0">{tones[tone].icon}</span>
      <span className="min-w-0 text-ink wrap-anywhere">{children}</span>
    </p>
  );
}

/** Nothing here yet: says so and offers the next step */
export function EmptyState({
  icon,
  title,
  text,
  action,
  level = 3,
}: {
  icon: ReactNode;
  title: string;
  text?: string;
  action?: ReactNode;
  /** The heading's level: 1 when the state is the whole page */
  level?: 1 | 2 | 3;
}) {
  const Heading = `h${level}` as const;
  return (
    <div className="grid justify-items-center gap-3 rounded-lg bg-card p-6 text-center">
      <span className="grid size-14 place-items-center rounded-full bg-muted text-ink-soft">
        {icon}
      </span>
      <Heading className="font-display text-lg font-heavy text-balance">{title}</Heading>
      {text ? <p className="max-w-prose text-ink-soft">{text}</p> : null}
      {action}
    </div>
  );
}

/** Something broke: what happened, what to do, and a way to try again */
export function ErrorState({
  title,
  text,
  onRetry,
  level = 3,
}: {
  title: string;
  text: string;
  onRetry?: () => void;
  level?: 1 | 2 | 3;
}) {
  const Heading = `h${level}` as const;
  return (
    <div
      role="alert"
      className="grid justify-items-center gap-3 rounded-lg bg-card p-6 text-center"
    >
      <span className="grid size-14 place-items-center rounded-full bg-danger-soft text-danger">
        <CircleAlert size={28} aria-hidden />
      </span>
      <Heading className="font-display text-lg font-heavy text-balance">{title}</Heading>
      <p className="max-w-prose text-ink-soft">{text}</p>
      {onRetry ? <Button onClick={onRetry}>{ru.ui.retry}</Button> : null}
    </div>
  );
}

/** The connection to the server dropped: a quiet chip, not a wall (DESIGN.md «Обновления в реальном времени») */
export function ConnectionLost({ compact = false }: { compact?: boolean }) {
  return (
    <p
      role="status"
      title={ru.ui.connectionLost}
      className={cx(
        'inline-flex shrink-0 items-center gap-2 rounded-full border-2 border-warning bg-warning-soft py-1 text-sm font-medium text-ink',
        compact ? 'min-h-11 min-w-11 justify-center px-2 desk:px-3' : 'px-3',
      )}
    >
      <WifiOff size={16} aria-hidden className="text-warning" />
      {/* In a phone's header only the icon shows; the words stay for screen readers */}
      <span className={compact ? 'sr-only desk:not-sr-only' : undefined}>
        {ru.ui.connectionLost}
      </span>
    </p>
  );
}
