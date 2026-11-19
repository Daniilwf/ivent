import { Sparkles } from 'lucide-react';
import { useCallback, useId, useRef, useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { moscowTime } from '../app/time';
import { answerOf, useLoaded } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { Confirm, FormDialog } from '../ui/Dialogs';
import { Field, Select, TextArea } from '../ui/Field';
import { Chip } from '../ui/Marks';
import { EmptyState, Notice } from '../ui/States';
import { parseWhole, refusal } from './actions';
import { packOf } from './content/contentModel';

type Event = Schemas['GlobalEventView'];
type Goal = Schemas['GlobalGoalRequest'];
type EventOption = { id: string; name: string };
const t = ru.admin.stage5Operations.globalEvents;

export function GlobalEventsSection({ seasonId, version }: { seasonId: string; version: number }) {
  const loaded = useLoaded<Event[] | null>(
    useCallback(async () => {
      const answer = await api.GET('/api/seasons/{seasonId}/global-events', {
        params: { path: { seasonId } },
      });
      return rejectionCode(answer.error) === 'feature.disabled'
        ? { kind: 'ready' as const, value: null }
        : answerOf(answer);
    }, [seasonId]),
    { version },
  );

  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {(events, reload) =>
        events && (
          <Events
            key={seasonId}
            seasonId={seasonId}
            events={events}
            version={version}
            reload={reload}
          />
        )
      }
    </AsyncState>
  );
}

function Events({
  events,
  seasonId,
  version,
  reload,
}: {
  events: Event[];
  seasonId: string;
  version: number;
  reload: () => void;
}) {
  const [launchOpen, setLaunchOpen] = useState(false);
  const [done, setDone] = useState<string>();
  const trigger = useRef<HTMLButtonElement>(null);

  return (
    <div className="grid gap-4" data-testid="admin-global-events">
      <Button
        ref={trigger}
        className="justify-self-start"
        variant="main"
        data-testid="global-event-open-launch"
        onClick={() => {
          setLaunchOpen(true);
        }}
      >
        {t.launch}
      </Button>
      {done && <Notice tone="success">{done}</Notice>}
      {launchOpen && (
        <LaunchEvent
          seasonId={seasonId}
          version={version}
          trigger={trigger}
          close={() => {
            setLaunchOpen(false);
          }}
          onDone={() => {
            setLaunchOpen(false);
            setDone(t.launched);
            reload();
          }}
        />
      )}
      {events.length === 0 ? (
        <EmptyState level={2} icon={<Sparkles size={28} aria-hidden />} title={t.empty} />
      ) : (
        <ul className="grid gap-3">
          {events.map((event) => (
            <li key={event.id}>
              <EventRow
                event={event}
                seasonId={seasonId}
                onDone={() => {
                  setDone(t.endDone);
                  reload();
                }}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function EventRow({
  event,
  seasonId,
  onDone,
}: {
  event: Event;
  seasonId: string;
  onDone: () => void;
}) {
  const [endOpen, setEndOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const goal = event.goal;

  async function end() {
    setBusy(true);
    setError(undefined);
    try {
      const answer = await api.POST(
        '/api/admin/seasons/{seasonId}/global-events/{globalEventId}/end',
        {
          params: { path: { seasonId, globalEventId: event.id } },
          body: { commandId: newCommandId() },
        },
      );
      if (answer.data) {
        setEndOpen(false);
        onDone();
      } else setError(refusal(answer));
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <article className="grid gap-3 rounded-lg bg-card p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <h2 className="min-w-0 font-display text-lg font-heavy wrap-anywhere">{event.name}</h2>
        <Chip tone={event.status === 'active' ? 'info' : 'muted'}>{t[event.status]}</Chip>
      </div>
      {event.description && <p className="wrap-anywhere">{event.description}</p>}
      <div className="grid gap-1 text-sm text-ink-soft">
        <p>
          {t.since} <time dateTime={event.startsAt}>{moscowTime(event.startsAt)}</time>
        </p>
        <p>
          {t.until} <time dateTime={event.endsAt}>{moscowTime(event.endsAt)}</time>
        </p>
      </div>
      {goal && (
        <div className="flex flex-wrap items-center gap-2">
          <p>
            {ru.globalEvents.progress(goal.progress, goal.target)}{' '}
            {ru.globalEvents.metrics[goal.metric](goal.target)}
          </p>
          {goal.reached && <Chip tone="success">{ru.globalEvents.reached}</Chip>}
        </div>
      )}
      {event.status === 'active' && (
        <div>
          <Confirm
            trigger={<Button data-testid="global-event-open-end">{t.end}</Button>}
            title={t.endTitle(event.name)}
            consequences={[t.endConsequence]}
            confirm={t.end}
            onConfirm={() => void end()}
            open={endOpen}
            onOpenChange={(open) => {
              if (!busy) {
                setError(undefined);
                setEndOpen(open);
              }
            }}
            busy={busy}
            testId="global-event-end"
          >
            {error && <Notice tone="danger">{error}</Notice>}
          </Confirm>
        </div>
      )}
    </article>
  );
}

function LaunchEvent({
  seasonId,
  version,
  trigger,
  close,
  onDone,
}: {
  seasonId: string;
  version: number;
  trigger: React.RefObject<HTMLButtonElement | null>;
  close: () => void;
  onDone: () => void;
}) {
  const formId = useId();
  const [object, setObject] = useState('');
  const [duration, setDuration] = useState('');
  const [metric, setMetric] = useState<Goal['metric'] | ''>('');
  const [target, setTarget] = useState('');
  const [reward, setReward] = useState('');
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const loaded = useLoaded<EventOption[]>(
    useCallback(async () => {
      const answer = await api.GET('/api/admin/seasons/{seasonId}/content', {
        params: { path: { seasonId } },
      });
      if (!answer.data) return answerOf<EventOption[]>({ response: answer.response });
      const options = packOf(answer.data.pack).objects.flatMap((entry) =>
        entry.kind === 'event' && typeof entry.id === 'string' && typeof entry.name === 'string'
          ? [{ id: entry.id, name: entry.name }]
          : [],
      );
      return { kind: 'ready' as const, value: options };
    }, [seasonId]),
    { version },
  );

  async function launch() {
    setError(undefined);
    if (loaded.kind !== 'ready' || !loaded.value.some(({ id }) => id === object)) {
      setError(t.chooseFirst);
      return;
    }
    const durationHours = parseWhole(duration);
    if (durationHours === null || durationHours < 1 || durationHours > 24 * 366) {
      setError(t.durationProblem);
      return;
    }
    let goal: Goal | undefined;
    if (metric) {
      const goalTarget = parseWhole(target);
      if (goalTarget === null || goalTarget < 1 || goalTarget > 1_000_000) {
        setError(t.targetProblem);
        return;
      }
      const effect = objectJson(reward);
      if (effect === null) {
        setError(t.rewardProblem);
        return;
      }
      goal = { metric, target: goalTarget, reward: effect };
    }
    setBusy(true);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/global-events', {
        params: { path: { seasonId } },
        body: { commandId: newCommandId(), objectId: object, durationHours, goal: goal ?? null },
      });
      if (answer.data) onDone();
      else
        setError(
          rejectionCode(answer.error) === 'content.invalid' ? t.launchInvalid : refusal(answer),
        );
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <FormDialog
      open
      onOpenChange={(open) => {
        if (!open && !busy) close();
      }}
      title={t.launchTitle}
      returnFocus={trigger}
      footer={
        <>
          {error && <Notice tone="danger">{error}</Notice>}
          <Button
            variant="main"
            type="submit"
            form={formId}
            loading={busy}
            disabled={loaded.kind !== 'ready' || loaded.value.length === 0}
            data-testid="global-event-launch"
          >
            {t.launch}
          </Button>
        </>
      }
    >
      <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle} rows={1}>
        {(options) =>
          options.length === 0 ? (
            <p>{t.noContent}</p>
          ) : (
            <form
              id={formId}
              className="grid gap-3"
              noValidate
              onSubmit={(e) => {
                e.preventDefault();
                if (!busy) void launch();
              }}
            >
              <Select
                label={t.object}
                value={object}
                data-testid="global-event-object"
                disabled={busy}
                onChange={(e) => {
                  setObject(e.target.value);
                }}
              >
                <option value="">{t.choose}</option>
                {options.map(({ id, name }) => (
                  <option value={id} key={id}>
                    {name}
                  </option>
                ))}
              </Select>
              <Field
                label={t.duration}
                type="number"
                min={1}
                max={24 * 366}
                value={duration}
                data-testid="global-event-duration"
                disabled={busy}
                onChange={(e) => {
                  setDuration(e.target.value);
                }}
              />
              <Select
                label={t.goal}
                value={metric}
                data-testid="global-event-goal"
                disabled={busy}
                onChange={(e) => {
                  const value = e.target.value;
                  setMetric(value === 'hours' || value === 'runs' ? value : '');
                }}
              >
                <option value="">{t.noGoal}</option>
                <option value="hours">{t.hours}</option>
                <option value="runs">{t.runs}</option>
              </Select>
              {metric && (
                <>
                  <Field
                    label={t.target}
                    type="number"
                    min={1}
                    max={1_000_000}
                    value={target}
                    data-testid="global-event-target"
                    disabled={busy}
                    onChange={(e) => {
                      setTarget(e.target.value);
                    }}
                  />
                  <TextArea
                    label={t.reward}
                    hint={t.rewardHint}
                    value={reward}
                    disabled={busy}
                    onChange={(e) => {
                      setReward(e.target.value);
                    }}
                    rows={3}
                  />
                </>
              )}
            </form>
          )
        }
      </AsyncState>
    </FormDialog>
  );
}

/** A reward may be omitted or supplied as one JSON object. */
function objectJson(text: string): object | null | undefined {
  if (text.trim() === '') return undefined;
  try {
    const value: unknown = JSON.parse(text);
    return value && typeof value === 'object' && !Array.isArray(value) ? value : null;
  } catch {
    return null;
  }
}
