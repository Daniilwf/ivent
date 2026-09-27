import { Download } from 'lucide-react';
import { useCallback, useRef, useState, type SyntheticEvent } from 'react';
import { api, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { buttonClass } from '../ui/buttonStyles';
import { ConfirmDanger } from '../ui/Dialogs';
import { Field } from '../ui/Field';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { refusal } from './actions';
import { moscowInput } from '../app/time';
import { newCommandId } from '../api/commands';
import { AsyncState } from '../ui/AsyncState';
import { answerOf, useLoaded } from '../app/useLoaded';

const t = ru.admin.season;

type Status = Schemas['SeasonStatus'];
type Message = { tone: 'success' | 'danger' | 'info'; text: string } | null;

const next: Partial<Record<Status, Status>> = {
  draft: 'active',
  active: 'closing',
  closing: 'finished',
  finished: 'archived',
};

/** The season's lifecycle, deadline, archive and integrity; the list of seasons and a new one */
export function SeasonSection({
  seasonId,
  version,
  onPick,
}: {
  seasonId: string | null;
  version: number;
  onPick: (seasonId: string) => void;
}) {
  const loaded = useLoaded(
    useCallback(async () => {
      const [list, season] = await Promise.all([
        api.GET('/api/seasons'),
        seasonId
          ? api.GET('/api/seasons/{seasonId}', { params: { path: { seasonId } } })
          : Promise.resolve(null),
      ]);
      if (!list.data) return answerOf(list);
      if (season && !season.data) return answerOf(season);
      return {
        kind: 'ready' as const,
        value: { list: list.data, season: season?.data ?? null },
      };
    }, [seasonId]),
    { version },
  );
  const [message, setMessage] = useState<Message>(null);

  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {({ list, season }, reload) => {
        const done = (text: string) => {
          setMessage({ tone: 'success', text });
          reload();
        };
        const failed = (text: string) => {
          setMessage({ tone: 'danger', text });
        };
        return (
          <div className="grid gap-6" data-testid="admin-season">
            {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
            {season && seasonId ? (
              <>
                <StatusPanel seasonId={seasonId} season={season} onDone={done} onFailed={failed} />
                <DeadlinePanel
                  seasonId={seasonId}
                  season={season}
                  onDone={done}
                  onFailed={failed}
                />
                <Panel title={t.transfer}>
                  <p className="max-w-prose text-ink-soft">{t.exportLead}</p>
                  <div>
                    <a
                      href={`/api/admin/seasons/${seasonId}/export`}
                      download
                      data-testid="season-export"
                      className={buttonClass('quiet')}
                    >
                      <Download size={20} aria-hidden />
                      {t.export}
                    </a>
                  </div>
                  <p className="max-w-prose text-ink-soft">{t.importLead}</p>
                </Panel>
                <IntegrityPanel seasonId={seasonId} />
              </>
            ) : null}
            <Panel title={t.seasons} data-testid="season-list">
              <ul className="grid gap-2">
                {list.map((s) => (
                  <li
                    key={s.id}
                    className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-md bg-page px-3 py-2"
                  >
                    <span className="mr-auto grid">
                      <span className="font-bold">{s.name}</span>
                      <span className="text-sm text-ink-soft">{ru.seasonStatus[s.status]}</span>
                    </span>
                    {s.id === seasonId ? (
                      <span className="text-sm font-medium">{t.viewing}</span>
                    ) : (
                      <Button
                        data-testid={`season-open-${s.id}`}
                        onClick={() => {
                          setMessage(null);
                          onPick(s.id);
                        }}
                      >
                        {t.openHere}
                      </Button>
                    )}
                  </li>
                ))}
              </ul>
            </Panel>
            <CreateSeason
              onCreated={(id, name) => {
                onPick(id);
                done(t.created(name));
              }}
            />
          </div>
        );
      }}
    </AsyncState>
  );
}

type Props = {
  seasonId: string;
  season: Schemas['SeasonView'];
  onDone: (text: string) => void;
  onFailed: (text: string) => void;
};

function StatusPanel({ seasonId, season, onDone, onFailed }: Props) {
  const to = next[season.status];
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const step = season.status in t.next ? t.next[season.status as keyof typeof t.next] : null;

  async function move() {
    if (!to) return;
    setBusy(true);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/status', {
        params: { path: { seasonId } },
        body: { commandId: newCommandId(), to },
      });
      if (answer.data) onDone(t.moved(ru.seasonStatus[to] ?? to));
      else onFailed(refusal(answer));
    } catch {
      onFailed(ru.admin.failed);
    } finally {
      setOpen(false);
      setBusy(false);
    }
  }

  return (
    <Panel title={t.current(season.name)} data-testid="season-status">
      <p>
        {t.status}: <span className="font-bold">{ru.seasonStatus[season.status]}</span>
      </p>
      {step && to ? (
        <div>
          <ConfirmDanger
            open={open}
            onOpenChange={setOpen}
            trigger={<Button data-testid="season-next">{step.label}</Button>}
            title={t.nextTitle(step.label)}
            consequences={step.consequences}
            confirm={step.label}
            busy={busy}
            onConfirm={() => void move()}
          />
        </div>
      ) : null}
    </Panel>
  );
}

function DeadlinePanel({ seasonId, season, onDone, onFailed }: Props) {
  const [value, setValue] = useState('');
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<'set' | 'remove' | null>(null);
  const open = season.status === 'draft' || season.status === 'active';

  async function send(deadline: string | null) {
    setBusy(deadline ? 'set' : 'remove');
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/deadline', {
        params: { path: { seasonId } },
        body: { commandId: newCommandId(), deadline },
      });
      if (answer.data) {
        setValue('');
        onDone(deadline ? t.deadlineSaved : t.deadlineRemoved);
      } else onFailed(refusal(answer));
    } catch {
      onFailed(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  function submit(e: SyntheticEvent) {
    e.preventDefault();
    if (value === '') {
      setError(t.deadlineRequired);
      return;
    }
    setError(undefined);
    void send(moscowInput(value));
  }

  return (
    <Panel title={t.deadline} data-testid="season-deadline-panel">
      <p className="font-bold">{season.deadline ? moscowTime(season.deadline) : t.noDeadline}</p>
      {open ? (
        <form className="grid gap-3" onSubmit={submit} noValidate>
          <Field
            type="datetime-local"
            label={t.deadlineField}
            hint={t.deadlineHint}
            value={value}
            error={error}
            data-testid="deadline-input"
            onChange={(e) => {
              setValue(e.target.value);
            }}
          />
          <div className="flex flex-wrap items-center gap-3">
            <Button
              type="submit"
              loading={busy === 'set'}
              disabled={busy === 'remove'}
              data-testid="deadline-save"
            >
              {t.setDeadline}
            </Button>
            {season.deadline ? (
              <Button
                variant="dangerLink"
                loading={busy === 'remove'}
                disabled={busy === 'set'}
                data-testid="deadline-remove"
                onClick={() => void send(null)}
              >
                {t.removeDeadline}
              </Button>
            ) : null}
          </div>
        </form>
      ) : null}
    </Panel>
  );
}

function IntegrityPanel({ seasonId }: { seasonId: string }) {
  const [busy, setBusy] = useState(false);
  const [report, setReport] = useState<Schemas['IntegrityReport'] | null>(null);
  const [error, setError] = useState<string>();

  async function check() {
    setBusy(true);
    setError(undefined);
    try {
      const answer = await api.GET('/api/admin/seasons/{seasonId}/integrity', {
        params: { path: { seasonId } },
      });
      if (answer.data) setReport(answer.data);
      else setError(refusal(answer));
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title={t.integrity} data-testid="season-integrity">
      <p className="max-w-prose text-ink-soft">{t.integrityLead}</p>
      {report ? (
        !report.settled ? (
          <Notice tone="info">{t.unsettled}</Notice>
        ) : report.isIntact ? (
          <Notice tone="success">{t.intact(report.lastSequence)}</Notice>
        ) : (
          <div className="grid gap-2">
            <Notice tone="danger">{t.notIntact}</Notice>
            <ul className="grid list-disc gap-1 pl-5 text-sm break-all">
              {report.differences.map((d) => (
                <li key={d}>{d}</li>
              ))}
            </ul>
          </div>
        )
      ) : null}
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <div>
        <Button loading={busy} data-testid="integrity-check" onClick={() => void check()}>
          {t.check}
        </Button>
      </div>
    </Panel>
  );
}

function CreateSeason({ onCreated }: { onCreated: (id: string, name: string) => void }) {
  const [name, setName] = useState('');
  const [error, setError] = useState<string>();
  const [failure, setFailure] = useState<string>();
  const [busy, setBusy] = useState(false);
  const pending = useRef<{ name: string; id: string } | null>(null);

  async function submit(e: SyntheticEvent) {
    e.preventDefault();
    const clean = name.trim();
    if (clean === '') {
      setError(ru.ui.nameRequired);
      return;
    }
    setError(undefined);
    setFailure(undefined);
    setBusy(true);
    // The new season's id stays until the server answers: a retry after a lost answer creates one season (D-68)
    const seasonId = pending.current?.name === clean ? pending.current.id : crypto.randomUUID();
    pending.current = { name: clean, id: seasonId };
    try {
      const answer = await api.POST('/api/admin/seasons', {
        body: { commandId: newCommandId(), seasonId, name: clean },
      });
      pending.current = null;
      if (answer.data) {
        setName('');
        onCreated(seasonId, clean);
      } else setFailure(refusal(answer));
    } catch {
      setFailure(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title={t.create}>
      <p className="max-w-prose text-ink-soft">{t.createLead}</p>
      <form className="grid gap-3" onSubmit={(e) => void submit(e)} noValidate>
        <Field
          label={t.name}
          value={name}
          maxLength={100}
          error={error}
          data-testid="season-name"
          onChange={(e) => {
            setName(e.target.value);
          }}
        />
        {failure ? <Notice tone="danger">{failure}</Notice> : null}
        <div>
          <Button type="submit" loading={busy} data-testid="season-create">
            {t.createSubmit}
          </Button>
        </div>
      </form>
    </Panel>
  );
}
