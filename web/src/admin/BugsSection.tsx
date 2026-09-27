import { Bug, Download } from 'lucide-react';
import { useCallback, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Badge } from '../ui/Marks';
import { Button } from '../ui/Button';
import { EmptyState, Notice } from '../ui/States';
import { cx } from '../ui/cx';
import { refusal } from './actions';
import { newCommandId } from '../api/commands';
import { Loading } from './common';
import { answerOf, useLoaded } from '../app/useLoaded';

const t = ru.admin.bugs;

type Report = Schemas['BugReportView'];
type Status = Schemas['BugReportStatus'];
type Filter = Status | 'all';

const filters: Filter[] = ['new', 'inWork', 'closed', 'all'];

// The next step of a report: new → in work (docs/BUGS.md) → closed; a closed one can be opened again
const moves: Record<Status, { to: Status; label: string }> = {
  new: { to: 'inWork', label: t.toWork },
  inWork: { to: 'closed', label: t.close },
  closed: { to: 'new', label: t.reopen },
};

/** The bug reports from the «Сообщить о баге» button, by status, with the export for the agent */
export function BugsSection() {
  const [filter, setFilter] = useState<Filter>('new');
  const loaded = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/admin/bug-reports', {
            params: { query: filter === 'all' ? {} : { status: filter } },
          }),
        ),
      [filter],
    ),
  );
  const [message, setMessage] = useState<{ tone: 'success' | 'danger'; text: string } | null>(null);

  return (
    <div className="grid gap-4" data-testid="admin-bugs">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div role="group" aria-label={t.filter} className="flex flex-wrap gap-2">
          {filters.map((f) => (
            <button
              key={f}
              type="button"
              aria-pressed={filter === f}
              data-testid={`bugs-filter-${f}`}
              onClick={() => {
                setFilter(f);
                setMessage(null);
              }}
              className={cx(
                'min-h-11 cursor-pointer rounded-full border-2 border-ink px-4 font-medium is-focus:focus-ring',
                filter === f ? 'bg-ink text-on-color' : 'bg-card text-ink is-hover:bg-page',
              )}
            >
              {t.filters[f]}
            </button>
          ))}
        </div>
        <a
          href={`/api/admin/bug-reports/export${filter === 'all' ? '' : `?status=${filter}`}`}
          download
          data-testid="bugs-export"
          className="inline-flex min-h-11 items-center gap-2 rounded-full px-1 font-bold underline underline-offset-4 is-focus:focus-ring"
        >
          <Download size={18} aria-hidden />
          {t.export}
        </a>
      </div>
      {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
      <Loading loaded={loaded}>
        {(reports, reload) =>
          reports.length === 0 ? (
            <EmptyState
              level={2}
              icon={<Bug size={28} aria-hidden />}
              title={t.emptyTitle}
              text={t.emptyText}
            />
          ) : (
            <ul className="grid gap-3">
              {reports.map((report) => (
                <li key={report.id}>
                  <ReportCard
                    report={report}
                    onDone={(text) => {
                      setMessage({ tone: 'success', text });
                      reload();
                    }}
                    onFailed={(text) => {
                      setMessage({ tone: 'danger', text });
                    }}
                  />
                </li>
              ))}
            </ul>
          )
        }
      </Loading>
    </div>
  );
}

function ReportCard({
  report,
  onDone,
  onFailed,
}: {
  report: Report;
  onDone: (text: string) => void;
  onFailed: (text: string) => void;
}) {
  const [busy, setBusy] = useState(false);
  const move = moves[report.status];
  const actions = report.context.actions ?? [];
  const errors = report.context.errors ?? [];

  async function change() {
    setBusy(true);
    try {
      const answer = await api.PUT('/api/admin/bug-reports/{reportId}/status', {
        params: { path: { reportId: report.id } },
        body: { commandId: newCommandId(), status: move.to },
      });
      if (answer.data) onDone(t.moved(t.statuses[move.to]));
      else onFailed(refusal(answer));
    } catch {
      onFailed(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <article
      className="grid gap-3 rounded-lg bg-card p-4 wrap-anywhere"
      data-testid={`bug-${report.id}`}
    >
      <div className="grid gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone="muted">{t.statuses[report.status]}</Badge>
          <span className="text-sm text-ink-soft">
            {t.from(report.author, moscowTime(report.createdAt))}
          </span>
        </div>
        <p className="whitespace-pre-line">{report.text}</p>
        <p className="text-sm text-ink-soft break-all">{t.page(report.page)}</p>
      </div>
      {report.screenshot ? (
        <a
          href={report.screenshot.url}
          target="_blank"
          rel="noreferrer noopener"
          className="block w-fit rounded-md is-focus:focus-ring"
        >
          <img
            src={report.screenshot.thumbnailUrl}
            alt={t.screenshot}
            width={160}
            height={120}
            className="h-30 w-40 rounded-md border-2 border-ink object-cover"
          />
        </a>
      ) : null}
      <details className="grid gap-2">
        <summary className="min-h-11 cursor-pointer content-center rounded-sm font-bold is-focus:focus-ring">
          {t.context}
        </summary>
        <div className="grid gap-2 pt-2 text-sm">
          {report.context.userAgent ? <p>{t.browser(report.context.userAgent)}</p> : null}
          {report.context.viewport ? <p>{t.viewport(report.context.viewport)}</p> : null}
          {actions.length > 0 ? (
            <>
              <p className="font-bold">{t.actions}</p>
              <ul className="grid list-disc gap-1 pl-5 break-all">
                {actions.map((a, i) => (
                  <li key={i}>{a.text}</li>
                ))}
              </ul>
            </>
          ) : null}
          {errors.length > 0 ? (
            <>
              <p className="font-bold">{t.errors}</p>
              <ul className="grid list-disc gap-1 pl-5 break-all">
                {errors.map((a, i) => (
                  <li key={i}>{a.text}</li>
                ))}
              </ul>
            </>
          ) : null}
        </div>
      </details>
      <div>
        <Button loading={busy} data-testid="bug-move" onClick={() => void change()}>
          {move.label}
        </Button>
      </div>
    </article>
  );
}
