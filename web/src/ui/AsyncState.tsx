import type { ReactNode } from 'react';
import type { Loaded } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { Skeleton } from './Progress';
import { ErrorState } from './States';

/**
 * A page's or a section's data as it comes (D-202), one way everywhere: the skeleton while loading, the error with a
 * retry, «not found» when the page says what that is (otherwise it is an error too), or the content.
 */
export function AsyncState<T>({
  loaded,
  skeleton,
  rows = 3,
  errorTitle,
  level = 2,
  notFound,
  children,
}: {
  loaded: Loaded<T> & { reload: () => void };
  /** The page's own grey shapes; by default a few grey cards */
  skeleton?: ReactNode;
  rows?: number;
  errorTitle: string;
  /** The heading level of the error: 1 when it stands for the whole page */
  level?: 1 | 2;
  notFound?: ReactNode;
  children: (data: T, reload: () => void) => ReactNode;
}) {
  if (loaded.kind === 'loading')
    return (
      skeleton ?? (
        <div className="grid gap-3" aria-busy="true" data-testid="loading">
          <span className="sr-only">{ru.ui.loading}</span>
          {Array.from({ length: rows }, (_, i) => (
            <Skeleton key={i} className="h-24 rounded-lg" />
          ))}
        </div>
      )
    );
  if (loaded.kind === 'notFound' && notFound) return <>{notFound}</>;
  if (loaded.kind !== 'ready')
    return (
      <ErrorState
        level={level}
        title={errorTitle}
        text={ru.shell.loadErrorText}
        onRetry={loaded.reload}
      />
    );
  return <>{children(loaded.value, loaded.reload)}</>;
}
