import { CalendarCog, Clock, Dices } from 'lucide-react';
import { useCallback, useState, type SyntheticEvent } from 'react';
import { api, type Schemas } from '../api/client';
import { navigate, paths } from '../app/router';
import { moscowInput, moscowTime } from '../app/time';
import { answerOf, useLoaded } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { ChoiceGroup, Field, Select } from '../ui/Field';
import { EmptyState, Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { commandLabel, refusal } from './actions';

const t = ru.admin.test;

type Message = { tone: 'success' | 'danger'; text: string };

/** The scenario that does not need a player: the deadline is the season's */
const seasonWide = 'deadline-in-hour';

/** The clock's shift from the real time in words: «+1 дн 2 ч», «−30 мин»; none — the real time */
function shiftText(minutes: number): string | null {
  const whole = Math.round(minutes);
  if (whole === 0) return null;
  const sign = whole > 0 ? '+' : '−';
  let left = Math.abs(whole);
  const days = Math.floor(left / 1440);
  left -= days * 1440;
  const hours = Math.floor(left / 60);
  const rest = left - hours * 60;
  const parts = [
    days ? t.days(days) : null,
    hours ? t.hours(hours) : null,
    rest ? t.minutes(rest) : null,
  ].filter((part): part is string => part !== null);
  return `${sign}${parts.join(' ')}`;
}

/**
 * The test tools (H9, D-221; SPEC «Тестовые эндпоинты», «Сценарии для ручной проверки»): load a ready situation onto
 * the season, move the site's clock, seed its randomness. Only where the test endpoints exist (Development and Test):
 * the admin screen does not offer the section anywhere else, and the strip on top of every page names the copy of the
 * site. Loading a scenario is the main action.
 */
export function TestToolsSection({ seasonId }: { seasonId: string | null }) {
  const loaded = useLoaded(useCallback(async () => answerOf(await api.GET('/api/test')), []));

  return (
    <div className="grid gap-4" data-testid="admin-test">
      <AsyncState loaded={loaded} rows={3} errorTitle={ru.admin.loadErrorTitle}>
        {(tools, reload) => (
          <>
            <ScenarioPanel seasonId={seasonId} scenarios={tools.scenarios} />
            <ClockPanel clock={tools.clock} onChanged={reload} />
            <RandomPanel random={tools.random} onChanged={reload} />
          </>
        )}
      </AsyncState>
    </div>
  );
}

function ScenarioPanel({ seasonId, scenarios }: { seasonId: string | null; scenarios: string[] }) {
  return (
    <Panel title={t.scenarios} data-testid="test-scenarios">
      {seasonId ? (
        <ScenarioForm seasonId={seasonId} scenarios={scenarios} />
      ) : (
        <EmptyState
          icon={<CalendarCog size={28} aria-hidden />}
          title={ru.admin.noSeasonTitle}
          text={t.noSeason}
          action={
            <Button
              onClick={() => {
                navigate(paths.admin('season'));
              }}
            >
              {ru.admin.toSeason}
            </Button>
          }
        />
      )}
    </Panel>
  );
}

function ScenarioForm({ seasonId, scenarios }: { seasonId: string; scenarios: string[] }) {
  const players = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/admin/seasons/{seasonId}/players', {
            params: { path: { seasonId } },
          }),
        ),
      [seasonId],
    ),
  );
  const [scenario, setScenario] = useState(scenarios[0] ?? '');
  const [picked, setPicked] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message | null>(null);

  async function load(playerId: string | null) {
    setBusy(true);
    setMessage(null);
    try {
      const answer = await api.POST('/api/test/seasons/{seasonId}/scenarios/{name}', {
        params: { path: { seasonId, name: scenario } },
        body: playerId ? { playerId } : {},
      });
      if (answer.data)
        setMessage({
          tone: 'success',
          text: t.loaded([...new Set(answer.data.commands.map(commandLabel))].join(', ')),
        });
      else setMessage({ tone: 'danger', text: refusal(answer) });
    } catch {
      setMessage({ tone: 'danger', text: ru.admin.failed });
    } finally {
      setBusy(false);
    }
  }

  return (
    <AsyncState loaded={players} rows={1} errorTitle={ru.admin.loadErrorTitle}>
      {(list) => {
        const needsPlayer = scenario !== seasonWide;
        const player = list.find((p) => p.id === picked) ?? list[0] ?? null;
        const blocked = needsPlayer && player === null;
        return (
          <form
            className="grid gap-4"
            noValidate
            onSubmit={(e: SyntheticEvent) => {
              e.preventDefault();
              if (!blocked) void load(needsPlayer ? (player?.id ?? null) : null);
            }}
          >
            <ChoiceGroup
              label={t.scenario}
              options={scenarios.map((name) => ({
                value: name,
                label: t.scenarioNames[name] ?? name,
              }))}
              value={scenario}
              onChange={(value) => {
                setScenario(value);
                setMessage(null);
              }}
              data-testid="scenario-choice"
            />
            {t.scenarioAbout[scenario] ? (
              <p className="max-w-prose text-ink-soft" data-testid="scenario-about">
                {t.scenarioAbout[scenario]}
              </p>
            ) : null}
            {needsPlayer ? (
              list.length > 0 ? (
                <Select
                  label={t.player}
                  className="max-w-100"
                  value={player?.id ?? ''}
                  data-testid="scenario-player"
                  onChange={(e) => {
                    setPicked(e.target.value);
                  }}
                >
                  {list.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </Select>
              ) : (
                <Notice tone="info">{t.noPlayers}</Notice>
              )
            ) : null}
            {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
            <div>
              <Button
                type="submit"
                variant="main"
                loading={busy}
                disabled={blocked}
                data-testid="scenario-load"
              >
                {t.load}
              </Button>
            </div>
          </form>
        );
      }}
    </AsyncState>
  );
}

function ClockPanel({
  clock,
  onChanged,
}: {
  clock: Schemas['TestClockView'];
  onChanged: () => void;
}) {
  const [moment, setMoment] = useState('');
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<Message | null>(null);
  const shift = shiftText(clock.shiftMinutes);

  async function move(key: string, body: Schemas['TestClockRequest'], done: string) {
    setBusy(key);
    setMessage(null);
    try {
      const answer = await api.POST('/api/test/clock', { body });
      if (answer.data) {
        setMessage({ tone: 'success', text: done });
        setMoment('');
        onChanged();
      } else setMessage({ tone: 'danger', text: refusal(answer) });
    } catch {
      setMessage({ tone: 'danger', text: ru.admin.failed });
    } finally {
      setBusy(null);
    }
  }

  return (
    <Panel title={t.clock} data-testid="test-clock">
      <div className="grid gap-1">
        <p className="flex items-center gap-2 font-display text-lg font-heavy tabular-nums">
          <Clock size={20} aria-hidden />
          <span data-testid="test-clock-now">{moscowTime(clock.now)}</span>
        </p>
        <p className="text-ink-soft" data-testid="test-clock-shift">
          {shift ? t.shift(shift) : t.realTime}
        </p>
      </div>
      <p className="max-w-prose text-sm text-ink-soft">{t.clockHint}</p>
      {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
      <div className="flex flex-wrap gap-3">
        <Button
          loading={busy === 'hour'}
          disabled={busy !== null && busy !== 'hour'}
          data-testid="clock-plus-hour"
          onClick={() => void move('hour', { advanceMinutes: 60, reset: false }, t.moved)}
        >
          {t.plusHour}
        </Button>
        <Button
          loading={busy === 'day'}
          disabled={busy !== null && busy !== 'day'}
          data-testid="clock-plus-day"
          onClick={() => void move('day', { advanceMinutes: 24 * 60, reset: false }, t.moved)}
        >
          {t.plusDay}
        </Button>
      </div>
      <form
        className="grid gap-3"
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          if (moment === '') {
            setError(t.moveToRequired);
            return;
          }
          setError(undefined);
          void move('moment', { moveTo: moscowInput(moment), reset: false }, t.moved);
        }}
      >
        <Field
          type="datetime-local"
          label={t.moveTo}
          hint={t.moveToHint}
          value={moment}
          error={error}
          data-testid="clock-moment"
          onChange={(e) => {
            setMoment(e.target.value);
          }}
        />
        <div className="flex flex-wrap items-center gap-3">
          <Button
            type="submit"
            loading={busy === 'moment'}
            disabled={busy !== null && busy !== 'moment'}
            data-testid="clock-move"
          >
            {t.moveToButton}
          </Button>
          {shift ? (
            <Button
              variant="link"
              loading={busy === 'reset'}
              disabled={busy !== null && busy !== 'reset'}
              data-testid="clock-reset"
              onClick={() => void move('reset', { reset: true }, t.wasReset)}
            >
              {t.reset}
            </Button>
          ) : null}
        </div>
      </form>
    </Panel>
  );
}

/** A whole number within the server's int: «42», «−7» with a real minus sign too */
function parseSeed(text: string): number | null {
  const clean = text.trim().replace('−', '-');
  if (!/^[-+]?\d{1,10}$/.test(clean)) return null;
  const value = Number(clean);
  return value >= -2_147_483_648 && value <= 2_147_483_647 ? value : null;
}

function RandomPanel({
  random,
  onChanged,
}: {
  random: Schemas['TestRandomView'];
  onChanged: () => void;
}) {
  const [text, setText] = useState('');
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<'seed' | 'unseed' | null>(null);
  const [message, setMessage] = useState<Message | null>(null);

  async function send(seed: number | null) {
    setBusy(seed === null ? 'unseed' : 'seed');
    setMessage(null);
    try {
      const answer = await api.POST('/api/test/random', { body: { seed } });
      if (answer.response.ok) {
        setMessage({ tone: 'success', text: seed === null ? t.wasUnseeded : t.seedSet });
        setText('');
        onChanged();
      } else setMessage({ tone: 'danger', text: refusal(answer) });
    } catch {
      setMessage({ tone: 'danger', text: ru.admin.failed });
    } finally {
      setBusy(null);
    }
  }

  const seed = random.seed ?? null;
  return (
    <Panel title={t.random} data-testid="test-random">
      <p className="flex items-center gap-2 font-bold" data-testid="test-random-state">
        <Dices size={20} aria-hidden />
        {seed === null ? t.unseeded : t.seeded(seed)}
      </p>
      {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
      <form
        className="grid gap-3"
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          const value = parseSeed(text);
          if (value === null) {
            setError(t.seedInvalid);
            return;
          }
          setError(undefined);
          void send(value);
        }}
      >
        <Field
          label={t.seed}
          hint={t.seedHint}
          inputMode="numeric"
          autoComplete="off"
          value={text}
          error={error}
          data-testid="random-seed"
          onChange={(e) => {
            setText(e.target.value);
          }}
        />
        <div className="flex flex-wrap items-center gap-3">
          <Button
            type="submit"
            loading={busy === 'seed'}
            disabled={busy === 'unseed'}
            data-testid="random-set"
          >
            {t.setSeed}
          </Button>
          {seed === null ? null : (
            <Button
              variant="link"
              loading={busy === 'unseed'}
              disabled={busy === 'seed'}
              data-testid="random-unseed"
              onClick={() => void send(null)}
            >
              {t.unseed}
            </Button>
          )}
        </div>
      </form>
    </Panel>
  );
}
