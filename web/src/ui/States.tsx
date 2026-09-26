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
        'flex items-start gap-2 rounded-md border-l-4 px-3 py-2 text-sm font-medium',
        tones[tone].box,
      )}
    >
      {tones[tone].icon}
      <span className="text-ink">{children}</span>
    </p>
  );
}

/** Nothing here yet: says so and offers the next step */
export function EmptyState({
  icon,
  title,
  text,
  action,
}: {
  icon: ReactNode;
  title: string;
  text?: string;
  action?: ReactNode;
}) {
  return (
    <div className="grid justify-items-center gap-3 rounded-lg bg-card p-6 text-center">
      <span className="grid size-14 place-items-center rounded-full bg-muted text-ink-soft">
        {icon}
      </span>
      <h3 className="font-display text-lg font-heavy text-balance">{title}</h3>
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
}: {
  title: string;
  text: string;
  onRetry?: () => void;
}) {
  return (
    <div
      role="alert"
      className="grid justify-items-center gap-3 rounded-lg border-l-4 border-danger bg-card p-6 text-center"
    >
      <span className="grid size-14 place-items-center rounded-full bg-danger-soft text-danger">
        <CircleAlert size={28} aria-hidden />
      </span>
      <h3 className="font-display text-lg font-heavy text-balance">{title}</h3>
      <p className="max-w-prose text-ink-soft">{text}</p>
      {onRetry ? <Button onClick={onRetry}>{ru.ui.retry}</Button> : null}
    </div>
  );
}

/** The connection to the server dropped: a quiet chip, not a wall (DESIGN.md «Обновления в реальном времени») */
export function ConnectionLost() {
  return (
    <p
      role="status"
      className="inline-flex items-center gap-2 rounded-full border-2 border-warning bg-warning-soft px-3 py-1 text-sm font-medium text-ink"
    >
      <WifiOff size={16} aria-hidden className="text-warning" />
      {ru.ui.connectionLost}
    </p>
  );
}
