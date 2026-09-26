import type { ReactNode } from 'react';
import { ru } from '../i18n/ru';
import { Skeleton } from '../ui/Progress';
import { ErrorState } from '../ui/States';
import type { Loaded } from './useLoad';

const t = ru.admin;

/** The loading skeleton, the error with a retry, or the section itself */
export function Loading<T>({
  loaded,
  rows = 3,
  children,
}: {
  loaded: Loaded<T>;
  rows?: number;
  children: (data: T, reload: () => void) => ReactNode;
}) {
  if (loaded.status === 'loading')
    return (
      <div className="grid gap-3" aria-busy="true" data-testid="admin-loading">
        <span className="sr-only">{ru.ui.loading}</span>
        {Array.from({ length: rows }, (_, i) => (
          <Skeleton key={i} className="h-24 rounded-lg" />
        ))}
      </div>
    );
  if (loaded.status === 'failed')
    return (
      <ErrorState
        level={2}
        title={t.loadErrorTitle}
        text={t.loadErrorText}
        onRetry={loaded.reload}
      />
    );
  return <>{children(loaded.data, loaded.reload)}</>;
}

/** A section's heading row: the title, a short lead and the count, if any */
export function SectionHead({ title, lead, id }: { title: string; lead: string; id?: string }) {
  return (
    <div className="grid gap-1">
      <h1
        id={id}
        tabIndex={-1}
        className="font-display text-2xl font-heavy text-balance outline-none"
      >
        {title}
      </h1>
      <p className="max-w-prose text-ink-soft">{lead}</p>
    </div>
  );
}
