import { Flag } from 'lucide-react';
import {
  useCallback,
  useDeferredValue,
  useId,
  useRef,
  useState,
  type SubmitEvent,
  type RefObject,
} from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { moscowInput, moscowTime } from '../app/time';
import { answerOf, useLoaded } from '../app/useLoaded';
import { coins, ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { Field, Select, TextArea } from '../ui/Field';
import { Chip } from '../ui/Marks';
import { EmptyState, Notice } from '../ui/States';
import { parseWhole, refusal } from './actions';

const t = ru.admin.stage5Operations.weeklyChallenges;
type Challenge = Schemas['WeeklyChallengeView'];
type Run = Schemas['GameRunView'];
type Trigger = RefObject<HTMLButtonElement | null>;

export function WeeklyChallengesSection({
  seasonId,
  version,
}: {
  seasonId: string;
  version: number;
}) {
  const loaded = useLoaded<Challenge[] | null>(
    useCallback(async () => {
      const answer = await api.GET('/api/seasons/{seasonId}/weekly-challenges', {
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
      {(challenges, reload) =>
        challenges && (
          <Challenges
            key={seasonId}
            seasonId={seasonId}
            challenges={challenges}
            version={version}
            reload={reload}
          />
        )
      }
    </AsyncState>
  );
}

function Challenges({
  seasonId,
  challenges,
  version,
  reload,
}: {
  seasonId: string;
  challenges: Challenge[];
  version: number;
  reload: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [done, setDone] = useState<string>();
  const trigger = useRef<HTMLButtonElement>(null);

  function changed(message: string) {
    setDone(message);
    reload();
  }

  return (
    <div className="grid gap-4" data-testid="admin-weekly-challenges">
      <Button
        ref={trigger}
        variant="main"
        className="justify-self-start"
        data-testid="weekly-challenge-open-set"
        onClick={() => {
          setOpen(true);
        }}
      >
        {t.set}
      </Button>
      {done && <Notice tone="success">{done}</Notice>}
      {open && (
        <SetChallenge
          seasonId={seasonId}
          trigger={trigger}
          close={() => {
            setOpen(false);
          }}
          onDone={() => {
            setOpen(false);
            changed(t.setDone);
          }}
        />
      )}
      {challenges.length === 0 ? (
        <EmptyState level={2} icon={<Flag size={28} aria-hidden />} title={t.empty} />
      ) : (
        <ul className="grid gap-3">
          {challenges.map((challenge) => (
            <li key={challenge.id}>
              <ChallengeRow
                seasonId={seasonId}
                challenge={challenge}
                version={version}
                onDone={changed}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function ChallengeRow({
  seasonId,
  challenge,
  version,
  onDone,
}: {
  seasonId: string;
  challenge: Challenge;
  version: number;
  onDone: (message: string) => void;
}) {
  const [markOpen, setMarkOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);

  return (
    <article className="grid gap-3 rounded-lg bg-card p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <h2 className="min-w-0 font-display text-lg font-heavy wrap-anywhere">{challenge.name}</h2>
        <Chip tone="muted">{t.checks[challenge.check]}</Chip>
      </div>
      <p className="wrap-anywhere">{challenge.description}</p>
      <div className="grid gap-1 text-sm text-ink-soft">
        <p>
          <time dateTime={challenge.startsAt}>{moscowTime(challenge.startsAt)}</time> –{' '}
          <time dateTime={challenge.endsAt}>{moscowTime(challenge.endsAt)}</time>
        </p>
        <p>{t.rewardOf(coins(challenge.rewardCoins))}</p>
      </div>
      <div>
        <h3 className="text-sm font-bold">{t.completed}</h3>
        {challenge.completedBy.length === 0 ? (
          <p className="text-sm text-ink-soft">{t.nobody}</p>
        ) : (
          <ul className="flex flex-wrap gap-x-3 gap-y-1 text-sm">
            {challenge.completedBy.map((player) => (
              <li className="min-w-0 wrap-anywhere" key={player.playerId}>
                {player.name}
              </li>
            ))}
          </ul>
        )}
      </div>
      {challenge.check === 'manual' && (
        <div>
          <Button
            ref={trigger}
            data-testid="weekly-challenge-open-mark"
            onClick={() => {
              setMarkOpen(true);
            }}
          >
            {t.mark}
          </Button>
          {markOpen && (
            <MarkChallenge
              seasonId={seasonId}
              challenge={challenge}
              version={version}
              trigger={trigger}
              close={() => {
                setMarkOpen(false);
              }}
              onDone={(playerName) => {
                setMarkOpen(false);
                onDone(t.marked(playerName));
              }}
            />
          )}
        </div>
      )}
    </article>
  );
}

function SetChallenge({
  seasonId,
  trigger,
  close,
  onDone,
}: {
  seasonId: string;
  trigger: Trigger;
  close: () => void;
  onDone: () => void;
}) {
  const formId = useId();
  // The retry middleware compares the rest of the request after excluding commandId.
  const [definitionId] = useState(newCommandId);
  const [check, setCheck] = useState<Schemas['ChallengeCheck']>('auto');
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  async function set(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    setError(undefined);
    const fields = new FormData(event.currentTarget);
    const name = field(fields, 'name');
    const description = field(fields, 'description');
    const startText = field(fields, 'start');
    const startsAt = moscowInput(startText);
    if (!name || !description || !startText || !Number.isFinite(Date.parse(startsAt))) {
      setError(t.invalid);
      return;
    }

    const coins = parseWhole(field(fields, 'coins'));
    if (coins === null || coins < 1 || coins > 2_147_483_647) {
      setError(t.rewardProblem);
      return;
    }
    // The content format always requires a condition. The manual verdict uses the written description.
    const game = check === 'auto' ? gameFilter(fields) : { minHours: 0 };
    if (typeof game === 'string') {
      setError(game);
      return;
    }

    const commandId = newCommandId();
    setBusy(true);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/weekly-challenges', {
        params: { path: { seasonId } },
        body: {
          commandId,
          startsAt,
          challenge: {
            id: definitionId,
            name,
            description,
            condition: { game },
            reward: { coins },
            check,
          },
        },
      });
      if (answer.data) onDone();
      else setError(refusal(answer));
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
      title={t.setTitle}
      returnFocus={trigger}
      footer={
        <>
          {error && <Notice tone="danger">{error}</Notice>}
          <Button
            type="submit"
            form={formId}
            variant="main"
            loading={busy}
            data-testid="weekly-challenge-set"
          >
            {t.set}
          </Button>
        </>
      }
    >
      <form id={formId} className="grid gap-3" noValidate onSubmit={(event) => void set(event)}>
        <Field
          label={t.name}
          name="name"
          maxLength={100}
          disabled={busy}
          data-testid="weekly-challenge-name"
        />
        <TextArea
          label={t.description}
          name="description"
          maxLength={1000}
          disabled={busy}
          rows={3}
          data-testid="weekly-challenge-description"
        />
        <Field
          label={t.starts}
          name="start"
          type="datetime-local"
          disabled={busy}
          data-testid="weekly-challenge-starts"
        />
        <Field
          label={t.reward}
          name="coins"
          type="number"
          min={1}
          disabled={busy}
          data-testid="weekly-challenge-coins"
        />
        <Select
          label={t.check}
          value={check}
          disabled={busy}
          data-testid="weekly-challenge-check"
          onChange={(e) => {
            setCheck(e.target.value === 'manual' ? 'manual' : 'auto');
          }}
        >
          <option value="auto">{t.checks.auto}</option>
          <option value="manual">{t.checks.manual}</option>
        </Select>
        {check === 'auto' && (
          <fieldset className="grid gap-3">
            <legend className="mb-2 font-bold">{t.filter}</legend>
            <Field label={t.tags} name="tags" disabled={busy} data-testid="weekly-challenge-tags" />
            <div className="grid gap-3 desk:grid-cols-2">
              <Field
                label={t.minHours}
                name="minHours"
                type="number"
                step="any"
                min={0}
                disabled={busy}
              />
              <Field
                label={t.maxHours}
                name="maxHours"
                type="number"
                step="any"
                min={0}
                disabled={busy}
              />
            </div>
            <Field label={t.beforeYear} name="beforeYear" type="number" min={1} disabled={busy} />
          </fieldset>
        )}
      </form>
    </FormDialog>
  );
}

function field(fields: FormData, key: string): string {
  const value = fields.get(key);
  return typeof value === 'string' ? value.trim() : '';
}

function gameFilter(fields: FormData) {
  const tags = field(fields, 'tags')
    .split(',')
    .map((tag) => tag.trim())
    .filter(Boolean);
  const optionalNumber = (key: string) => {
    const value = field(fields, key);
    return value ? Number(value) : undefined;
  };
  const minHours = optionalNumber('minHours');
  const maxHours = optionalNumber('maxHours');
  const releaseYearBefore = optionalNumber('beforeYear');
  if (
    tags.length === 0 &&
    minHours === undefined &&
    maxHours === undefined &&
    releaseYearBefore === undefined
  )
    return t.filterProblem;
  if (
    (minHours !== undefined && (!Number.isFinite(minHours) || minHours < 0)) ||
    (maxHours !== undefined && (!Number.isFinite(maxHours) || maxHours <= 0)) ||
    (minHours !== undefined && maxHours !== undefined && minHours > maxHours) ||
    (releaseYearBefore !== undefined &&
      (!Number.isInteger(releaseYearBefore) || releaseYearBefore < 1 || releaseYearBefore > 9999))
  )
    return t.rangeProblem;
  return { tags: tags.length > 0 ? tags : undefined, minHours, maxHours, releaseYearBefore };
}

function MarkChallenge({
  seasonId,
  challenge,
  version,
  trigger,
  close,
  onDone,
}: {
  seasonId: string;
  challenge: Challenge;
  version: number;
  trigger: Trigger;
  close: () => void;
  onDone: (playerName: string) => void;
}) {
  const [search, setSearch] = useState('');
  const query = useDeferredValue(search.trim());
  const [gameId, setGameId] = useState('');
  const [runId, setRunId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const games = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/pool', {
            params: { query: query ? { query, deleted: true } : { deleted: true } },
          }),
        ),
      [query],
    ),
  );
  const runs = useLoaded(
    useCallback(
      async () =>
        gameId
          ? answerOf(await api.GET('/api/pool/{gameId}/runs', { params: { path: { gameId } } }))
          : { kind: 'ready' as const, value: [] },
      [gameId],
    ),
    { version },
  );
  const candidates =
    runs.kind === 'ready' ? runs.value.filter((run) => eligible(run, seasonId, challenge)) : [];
  const chosen = candidates.find((run) => run.runId === runId);

  async function mark(run: Run) {
    setBusy(true);
    setError(undefined);
    try {
      const answer = await api.POST(
        '/api/admin/seasons/{seasonId}/weekly-challenges/{challengeId}/mark',
        {
          params: { path: { seasonId, challengeId: challenge.id } },
          body: { commandId: newCommandId(), playerId: run.playerId, runId: run.runId },
        },
      );
      if (answer.data) onDone(run.playerName);
      else setError(refusal(answer));
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
      title={t.markTitle(challenge.name)}
      returnFocus={trigger}
      footer={
        <>
          {error && <Notice tone="danger">{error}</Notice>}
          <Button
            variant="main"
            data-testid="weekly-challenge-mark"
            disabled={!chosen}
            loading={busy}
            onClick={() => {
              if (chosen && !busy) void mark(chosen);
            }}
          >
            {t.mark}
          </Button>
        </>
      }
    >
      <Field
        label={t.gameSearch}
        type="search"
        value={search}
        disabled={busy}
        onChange={(e) => {
          setSearch(e.target.value);
          setGameId('');
          setRunId('');
        }}
      />
      <AsyncState loaded={games} errorTitle={ru.admin.loadErrorTitle} rows={1}>
        {(options) =>
          options.length === 0 ? (
            <p>{t.noGames}</p>
          ) : (
            <Select
              label={t.game}
              value={gameId}
              data-testid="weekly-challenge-game"
              disabled={busy || search.trim() !== query}
              onChange={(e) => {
                setGameId(e.target.value);
                setRunId('');
              }}
            >
              <option value="">{t.chooseGame}</option>
              {options.map(({ id, title, isDeleted }) => (
                <option value={id} key={id}>
                  {isDeleted ? `${title} · ${ru.gamePage.deleted}` : title}
                </option>
              ))}
            </Select>
          )
        }
      </AsyncState>
      {gameId && (
        <AsyncState loaded={runs} errorTitle={ru.admin.loadErrorTitle} rows={1}>
          {() =>
            candidates.length === 0 ? (
              <p>{t.noRuns}</p>
            ) : (
              <Select
                label={t.run}
                value={chosen?.runId ?? ''}
                data-testid="weekly-challenge-run"
                disabled={busy}
                onChange={(e) => {
                  setRunId(e.target.value);
                }}
              >
                <option value="">{t.chooseRun}</option>
                {candidates.map((run) => (
                  <option key={run.runId} value={run.runId}>
                    {t.runOption(
                      run.playerName,
                      run.completedAt ? moscowTime(run.completedAt) : '',
                    )}
                  </option>
                ))}
              </Select>
            )
          }
        </AsyncState>
      )}
    </FormDialog>
  );
}

function eligible(run: Run, seasonId: string, challenge: Challenge): boolean {
  if (run.status !== 'completed' || run.seasonId !== seasonId || !run.completedAt) return false;
  const completed = Date.parse(run.completedAt);
  return (
    completed >= Date.parse(challenge.startsAt) &&
    completed < Date.parse(challenge.endsAt) &&
    !challenge.completedBy.some((player) => player.playerId === run.playerId)
  );
}
