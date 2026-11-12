import { LogOut, MonitorSmartphone } from 'lucide-react';
import { useCallback, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { moscowTime } from '../app/time';
import { answerOf, useLoaded } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { Badge } from '../ui/Marks';
import { Skeleton } from '../ui/Progress';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { deviceOf } from './devices';

const t = ru.sessions;

type Session = Schemas['SessionView'];
type Message = { tone: 'success' | 'danger'; text: string } | null;

/**
 * «Где я вошёл» on my own profile (D-466): the devices my account is open on, this one first; «Выйти» on another one,
 * «Выйти на остальных устройствах» for all but this one. Ending a session signs that device out at once.
 */
export function SessionsPanel({ onSignedOut }: { onSignedOut: () => void }) {
  const loaded = useLoaded(
    useCallback(async () => {
      try {
        return answerOf(await api.GET('/api/auth/sessions'));
      } catch {
        return { kind: 'failed' as const };
      }
    }, []),
    { onSignedOut },
  );

  return (
    <Panel title={t.title} data-testid="sessions">
      <p className="text-ink-soft">{t.lead}</p>
      <AsyncState loaded={loaded} errorTitle={t.errorTitle} skeleton={<SessionsSkeleton />}>
        {(sessions, reload) => <SessionList sessions={sessions} reload={reload} />}
      </AsyncState>
    </Panel>
  );
}

/** A loaded list: also what the styleguide shows */
export function SessionList({
  sessions,
  reload,
  now,
}: {
  sessions: Session[];
  reload: () => void;
  /** For the styleguide's fixed dates */
  now?: Date;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<Message>(null);
  const others = sessions.filter((s) => !s.current);

  async function run(key: string, step: () => Promise<string | null>) {
    setBusy(key);
    setMessage(null);
    try {
      const done = await step();
      setMessage(done ? { tone: 'success', text: done } : { tone: 'danger', text: t.failed });
      if (done) reload();
    } catch {
      setMessage({ tone: 'danger', text: t.failed });
    } finally {
      setBusy(null);
    }
  }

  const endOne = (id: string) =>
    run(id, async () => {
      const { response } = await api.POST('/api/auth/sessions/{sessionId}/end', {
        params: { path: { sessionId: id } },
        body: { commandId: newCommandId() },
      });
      // Already gone (ended elsewhere a moment ago) is what was asked for
      return response.ok || response.status === 404 ? t.ended : null;
    });

  const endOthers = () =>
    run('others', async () => {
      const { data } = await api.POST('/api/auth/sessions/end-others', {
        body: { commandId: newCommandId() },
      });
      return data ? t.endedOthers(data.ended) : null;
    });

  return (
    <div className="grid gap-3">
      {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
      <ul className="grid divide-y-2 divide-muted" data-testid="session-list">
        {sessions.map((session) => {
          const device = deviceOf(session.userAgent);
          return (
            <li
              key={session.id}
              className="grid grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-x-3 py-3"
              data-testid={session.current ? 'session-current' : 'session-other'}
            >
              <MonitorSmartphone size={24} aria-hidden className="text-ink-soft" />
              <div className="grid min-w-0 gap-1">
                <span className="flex flex-wrap items-center gap-2 font-bold wrap-anywhere">
                  {device}
                  {session.current ? <Badge tone="me">{t.thisDevice}</Badge> : null}
                </span>
                <span className="text-sm text-ink-soft">
                  {session.current
                    ? t.signedInAt(moscowTime(session.createdAt, now))
                    : t.lastSeen(moscowTime(session.lastSeenAt, now))}
                </span>
              </div>
              {session.current ? null : (
                <Button
                  variant="link"
                  aria-label={t.endLabel(device)}
                  loading={busy === session.id}
                  disabled={busy !== null && busy !== session.id}
                  data-testid="session-end"
                  onClick={() => void endOne(session.id)}
                >
                  {t.end}
                </Button>
              )}
            </li>
          );
        })}
      </ul>
      {others.length === 0 ? (
        <p className="text-sm text-ink-soft" data-testid="sessions-only-this">
          {t.onlyThis}
        </p>
      ) : (
        <div>
          <Button
            variant="quiet"
            // A long Russian label: it wraps on a phone instead of pushing the page wider
            className="text-center whitespace-normal!"
            icon={<LogOut size={20} aria-hidden />}
            loading={busy === 'others'}
            disabled={busy !== null && busy !== 'others'}
            data-testid="sessions-end-others"
            onClick={() => void endOthers()}
          >
            {t.endOthers}
          </Button>
        </div>
      )}
    </div>
  );
}

function SessionsSkeleton() {
  return (
    <div className="grid gap-3" aria-busy="true" data-testid="sessions-loading">
      <span className="sr-only">{ru.ui.loading}</span>
      <Skeleton className="h-12 w-full rounded-md" />
      <Skeleton className="h-12 w-full rounded-md" />
    </div>
  );
}
