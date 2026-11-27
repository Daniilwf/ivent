import { Vote } from 'lucide-react';
import { useCallback, useId, useRef, useState, type RefObject } from 'react';
import { api, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { answerOf, useLoaded } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { Checkbox, Select } from '../ui/Field';
import { EmptyState, Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { refusal } from './actions';

type Category = Schemas['VotedNomination'];
type Candidates = Schemas['NominationCandidatesView'];
type PollStatus = Schemas['PollStatus'];
const titles = ru.nomination.votedTitles;
const t = ru.nomination.admin;

function isCategory(value: unknown): value is Category {
  return typeof value === 'string' && Object.hasOwn(titles, value);
}

/** Only mounted for a closing season. Its configured ballot kinds are needed before offering the action. */
export function NominationSection({ seasonId, version }: { seasonId: string; version: number }) {
  const rules = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/seasons/{seasonId}/rules', {
            params: { path: { seasonId } },
          }),
        ),
      [seasonId],
    ),
    { version },
  );
  if (rules.kind !== 'ready') return null;

  const { features, nominations } = rules.value.ruleset;
  if (!features.seasonNominations || !features.polls) return null;
  const categories = Array.isArray(nominations.voted) ? nominations.voted.filter(isCategory) : [];
  if (categories.length === 0) return null;
  return <BallotPanel seasonId={seasonId} version={version} categories={categories} />;
}

function BallotPanel({
  seasonId,
  version,
  categories,
}: {
  seasonId: string;
  version: number;
  categories: Category[];
}) {
  const [open, setOpen] = useState(false);
  const [launched, setLaunched] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);

  return (
    <Panel title={t.title}>
      <p className="max-w-prose text-ink-soft">{t.lead}</p>
      {launched ? <Notice tone="success">{t.launched}</Notice> : null}
      <Button
        ref={trigger}
        className="justify-self-start"
        aria-haspopup="dialog"
        onClick={() => {
          setLaunched(false);
          setOpen(true);
        }}
      >
        {t.enter}
      </Button>
      {open ? (
        <BallotComposer
          seasonId={seasonId}
          version={version}
          categories={categories}
          trigger={trigger}
          close={() => {
            setOpen(false);
          }}
          onLaunched={() => {
            setOpen(false);
            setLaunched(true);
          }}
        />
      ) : null}
    </Panel>
  );
}

function BallotComposer({
  seasonId,
  version,
  categories,
  trigger,
  close,
  onLaunched,
}: {
  seasonId: string;
  version: number;
  categories: Category[];
  trigger: RefObject<HTMLButtonElement | null>;
  close: () => void;
  onLaunched: () => void;
}) {
  const formId = useId();
  const [category, setCategory] = useState<Category | ''>('');
  const [selected, setSelected] = useState<string[]>([]);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const sending = useRef(false);
  const pending = useRef<{ key: string; pollId: string; commandId: string } | null>(null);
  const polls = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/seasons/{seasonId}/polls', {
            params: { path: { seasonId } },
          }),
        ),
      [seasonId],
    ),
    { version },
  );
  const loaded = useLoaded<Candidates | null>(
    useCallback(
      async () =>
        category
          ? answerOf(
              await api.GET('/api/admin/seasons/{seasonId}/nominations/candidates', {
                params: { path: { seasonId }, query: { category } },
              }),
            )
          : { kind: 'ready', value: null },
      [seasonId, category],
    ),
    { version },
  );

  const blocked = new Map<Category, Exclude<PollStatus, 'cancelled'>>();
  if (polls.kind === 'ready')
    for (const poll of polls.value)
      if (poll.nomination && poll.status !== 'cancelled') blocked.set(poll.nomination, poll.status);
  const current = loaded.kind === 'ready' ? loaded.value : null;
  const available = current?.candidates;
  const candidateIds = selected.filter((id) => available?.some((candidate) => candidate.id === id));
  const blockedCategory = category && blocked.get(category);
  const canLaunch = Boolean(
    current &&
    category &&
    categories.includes(category) &&
    !blockedCategory &&
    polls.kind === 'ready' &&
    candidateIds.length >= current.minCandidates &&
    candidateIds.length <= current.maxCandidates,
  );

  async function launch() {
    if (!category || !canLaunch || sending.current) return;
    const key = JSON.stringify([category, candidateIds]);
    // Retry a lost response with the same command and poll IDs.
    const ids =
      pending.current?.key === key
        ? pending.current
        : { key, pollId: newCommandId(), commandId: newCommandId() };
    pending.current = ids;
    sending.current = true;
    setBusy(true);
    setError(undefined);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/nominations/polls', {
        params: { path: { seasonId } },
        body: { commandId: ids.commandId, pollId: ids.pollId, category, candidateIds },
      });
      pending.current = null;
      if (answer.data) onLaunched();
      else setError(refusal(answer));
    } catch {
      setError(ru.admin.failed);
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }

  return (
    <FormDialog
      open
      title={t.compose}
      returnFocus={trigger}
      onOpenChange={(next) => {
        if (!next && !sending.current) close();
      }}
      footer={
        <>
          {error ? <Notice tone="danger">{error}</Notice> : null}
          <Button type="submit" form={formId} variant="main" loading={busy} disabled={!canLaunch}>
            {t.launch}
          </Button>
        </>
      }
    >
      <AsyncState loaded={polls} rows={1} errorTitle={ru.polls.loadFailed}>
        {() =>
          categories.every((kind) => blocked.has(kind)) ? (
            <EmptyState level={2} icon={<Vote aria-hidden />} title={t.allStarted} />
          ) : (
            <form
              id={formId}
              className="grid gap-4"
              onSubmit={(event) => {
                event.preventDefault();
                void launch();
              }}
            >
              <Select
                label={t.category}
                value={category}
                disabled={busy}
                onChange={(event) => {
                  const value = event.target.value;
                  setCategory(isCategory(value) ? value : '');
                  setSelected([]);
                  setError(undefined);
                }}
              >
                <option value="">{t.choose}</option>
                {categories.map((kind) => {
                  const status = blocked.get(kind);
                  return (
                    <option key={kind} value={kind} disabled={Boolean(status)}>
                      {titles[kind]}
                      {status ? `, ${t[status]}` : ''}
                    </option>
                  );
                })}
              </Select>
              {blockedCategory ? (
                <Notice tone="info">{t[blockedCategory]}</Notice>
              ) : category ? (
                <AsyncState loaded={loaded} rows={1} errorTitle={t.candidateLoadFailed}>
                  {(list) =>
                    list ? (
                      <CandidateChoices
                        list={list}
                        selected={candidateIds}
                        busy={busy}
                        onToggle={(id, checked) => {
                          setSelected(
                            checked
                              ? [...candidateIds, id]
                              : candidateIds.filter((entry) => entry !== id),
                          );
                          setError(undefined);
                        }}
                      />
                    ) : null
                  }
                </AsyncState>
              ) : null}
            </form>
          )
        }
      </AsyncState>
    </FormDialog>
  );
}

function CandidateChoices({
  list,
  selected,
  busy,
  onToggle,
}: {
  list: Candidates;
  selected: string[];
  busy: boolean;
  onToggle: (id: string, checked: boolean) => void;
}) {
  const prefix = useId();
  const { candidates, minCandidates, maxCandidates } = list;
  if (candidates.length < minCandidates) {
    return (
      <EmptyState
        level={2}
        icon={<Vote aria-hidden />}
        title={t.tooFew}
        text={t.needCandidates(minCandidates)}
      />
    );
  }
  return (
    <fieldset className="grid min-w-0 gap-2">
      <legend className="font-bold">{t.candidates}</legend>
      <p className="text-sm text-ink-soft">
        {t.selection(minCandidates, maxCandidates)} {t.duration(list.durationHours)}
      </p>
      <p role="status" className="text-sm font-medium tabular-nums">
        {t.selected(selected.length, maxCandidates)}
      </p>
      <ul className="grid min-w-0 gap-2">
        {candidates.map(({ id, label, detail, thumbnailUrl }) => {
          const checked = selected.includes(id);
          const description = detail ? `${prefix}-${id}` : undefined;
          return (
            <li key={id} className="grid min-w-0 gap-1 rounded-md bg-page px-3 pb-2">
              <Checkbox
                aria-describedby={description}
                label={label}
                className="font-medium wrap-anywhere"
                checked={checked}
                disabled={busy || (!checked && selected.length >= maxCandidates)}
                onChange={(event) => {
                  onToggle(id, event.target.checked);
                }}
              />
              {thumbnailUrl ? (
                <img
                  className="aspect-video w-full max-w-56 rounded-md object-contain"
                  src={thumbnailUrl}
                  loading="lazy"
                  alt=""
                />
              ) : null}
              {detail ? (
                <p
                  id={description}
                  className="text-sm whitespace-pre-wrap wrap-anywhere text-ink-soft"
                >
                  {detail}
                </p>
              ) : null}
            </li>
          );
        })}
      </ul>
    </fieldset>
  );
}
