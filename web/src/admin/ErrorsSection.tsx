import { useCallback } from 'react';
import { ServerCrash } from 'lucide-react';
import { api } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { EmptyState } from '../ui/States';
import { AsyncState } from '../ui/AsyncState';
import { answerOf, useLoaded } from '../app/useLoaded';

const t = ru.admin.errors;

/** The error journal (D7): the latest unhandled exceptions with the request and the user */
export function ErrorsSection() {
  const loaded = useLoaded(
    useCallback(async () => answerOf(await api.GET('/api/admin/errors')), []),
  );
  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {(entries) =>
        entries.length === 0 ? (
          <EmptyState
            level={2}
            icon={<ServerCrash size={28} aria-hidden />}
            title={t.emptyTitle}
            text={t.emptyText}
          />
        ) : (
          <ul className="grid gap-3" data-testid="admin-errors">
            {entries.map((e) => (
              <li
                key={e.id}
                className="grid gap-2 rounded-lg bg-card p-4 wrap-anywhere"
                data-testid={`error-${e.id}`}
              >
                <p className="text-sm text-ink-soft">{moscowTime(e.at)}</p>
                <p className="font-bold break-all">
                  {e.method} {e.path}
                </p>
                <p>
                  {e.exceptionType}: {e.message}
                </p>
                <p className="text-sm text-ink-soft">
                  {t.user(e.userLogin)}. {t.traceId(e.traceId)}
                </p>
                <details>
                  <summary className="min-h-11 cursor-pointer content-center rounded-sm font-bold is-focus:focus-ring">
                    {t.trace}
                  </summary>
                  <pre className="overflow-x-auto rounded-md bg-page p-3 text-xs whitespace-pre-wrap break-all">
                    {e.stackTrace}
                  </pre>
                </details>
              </li>
            ))}
          </ul>
        )
      }
    </AsyncState>
  );
}
