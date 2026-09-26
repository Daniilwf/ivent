import type { ReactNode, Ref } from 'react';
import { ru } from '../i18n/ru';
import { Skeleton } from '../ui/Progress';
import { ErrorState } from '../ui/States';
import type { Loaded } from '../app/useLoaded';

const t = ru.admin;

/** The loading skeleton, the error with a retry, or the section itself */
export function Loading<T>({
  loaded,
  rows = 3,
  children,
}: {
  loaded: Loaded<T> & { reload: () => void };
  rows?: number;
  children: (data: T, reload: () => void) => ReactNode;
}) {
  if (loaded.kind === 'loading')
    return (
      <div className="grid gap-3" aria-busy="true" data-testid="admin-loading">
        <span className="sr-only">{ru.ui.loading}</span>
        {Array.from({ length: rows }, (_, i) => (
          <Skeleton key={i} className="h-24 rounded-lg" />
        ))}
      </div>
    );
  // An admin's section that is not there (a season deleted meanwhile) is a failure to load it too
  if (loaded.kind !== 'ready')
    return (
      <ErrorState
        level={2}
        title={t.loadErrorTitle}
        text={ru.shell.loadErrorText}
        onRetry={loaded.reload}
      />
    );
  return <>{children(loaded.value, loaded.reload)}</>;
}

/** A section's heading row: the title, a short lead and the count, if any */
export function SectionHead({
  title,
  lead,
  id,
  headingRef,
  action,
}: {
  title: string;
  lead: string;
  id?: string;
  /** The page's heading: it takes the focus when the page is opened (usePageHeading) */
  headingRef?: Ref<HTMLHeadingElement>;
  /** Beside the title: the list of sections on a phone */
  action?: ReactNode;
}) {
  return (
    <div className="grid gap-1">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1
          ref={headingRef}
          id={id}
          tabIndex={-1}
          className="min-w-0 font-display text-xl font-heavy text-balance outline-none desk:text-2xl"
        >
          {title}
        </h1>
        {action}
      </div>
      <p className="max-w-prose text-ink-soft">{lead}</p>
    </div>
  );
}
