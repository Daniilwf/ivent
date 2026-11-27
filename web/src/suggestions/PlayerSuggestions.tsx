import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type SyntheticEvent,
} from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { moscowTime } from '../app/time';
import { answerOf, useLoaded } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { Summary } from '../ui/Disclosure';
import { Checkbox, Field, Select, TextArea } from '../ui/Field';
import { Chip } from '../ui/Marks';
import { Notice } from '../ui/States';

export type PlayerSuggestionsProps = {
  seasonId: string;
  role: Schemas['CurrentUser']['role'];
  participating: boolean;
  seasonStatus: Schemas['SeasonView']['status'];
  onSignedOut: () => void;
};

const t = ru.playerSuggestions;
type Kind = Schemas['SuggestionKind'];
type Rules = Schemas['RulesView'];
const kinds: readonly Kind[] = ['game', 'event', 'item'];

/** The private box is independent of the turn: its reads never delay the main game action. */
export function PlayerSuggestions(props: PlayerSuggestionsProps) {
  return props.role === 'player' && props.participating ? (
    <SuggestionsForSeason key={props.seasonId} {...props} />
  ) : null;
}

function SuggestionsForSeason({ seasonId, onSignedOut, seasonStatus }: PlayerSuggestionsProps) {
  const version = useSeasonVersion(seasonId, {
    refreshOnJoin: true,
    throttleMs: 8_000,
    relevant: (update) =>
      update.types.includes('ruleset-changed') || update.types.includes('command-undone'),
  });
  const load = useCallback(async () => {
    const result = await api.GET('/api/seasons/{seasonId}/rules', {
      params: { path: { seasonId } },
    });
    return answerOf(result);
  }, [seasonId]);
  const rules = useLoaded(load, { version, onSignedOut });
  // Discovering an optional feature must not add a loading block or an unavailable feature to the turn.
  if (rules.kind === 'loading' || rules.kind === 'notFound') return null;
  return (
    <AsyncState loaded={rules} errorTitle={t.rulesFailed}>
      {(value, reloadRules) =>
        value.ruleset.features.suggestions ? (
          <SuggestionBox
            seasonId={seasonId}
            rules={value}
            seasonStatus={seasonStatus}
            onSignedOut={onSignedOut}
            reloadRules={reloadRules}
          />
        ) : null
      }
    </AsyncState>
  );
}

function SuggestionBox({
  seasonId,
  rules,
  seasonStatus,
  onSignedOut,
  reloadRules,
}: {
  seasonId: string;
  rules: Rules;
  seasonStatus: Schemas['SeasonView']['status'];
  onSignedOut: () => void;
  reloadRules: () => void;
}) {
  const heading = useId();
  const [kind, setKind] = useState<Kind>('game');
  const [text, setText] = useState('');
  const [title, setTitle] = useState('');
  const [tags, setTags] = useState('');
  const [hours, setHours] = useState('');
  const [year, setYear] = useState('');
  const [steam, setSteam] = useState('');
  const [coop, setCoop] = useState(false);
  const [condition, setCondition] = useState('');
  const [note, setNote] = useState('');
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState<{ tone: 'danger' | 'success'; text: string } | null>(null);
  const [blockedRules, setBlockedRules] = useState<Rules | null>(null);
  const write = useRef<AbortController | null>(null);
  const form = useRef<HTMLFormElement>(null);
  const focusReturn = useRef<HTMLElement | null>(null);
  const version = useSeasonVersion(seasonId, {
    refreshOnJoin: true,
    throttleMs: 8_000,
    relevant: (update) =>
      update.types.some(
        (type) =>
          type === 'suggestion-submitted' ||
          type === 'suggestion-reviewed' ||
          type === 'command-undone',
      ),
  });
  const load = useCallback(async () => {
    if (!rules.ruleset.features.suggestions) return { kind: 'ready', value: null } as const;
    const result = await api.GET('/api/seasons/{seasonId}/suggestions', {
      params: { path: { seasonId } },
    });
    if (rejectionCode(result.error) === 'feature.disabled' || result.response.status === 403)
      return { kind: 'ready', value: null } as const;
    return answerOf(result);
  }, [seasonId, rules]);
  const loaded = useLoaded<Schemas['SuggestionView'][] | null>(load, { version, onSignedOut });
  useEffect(() => () => write.current?.abort(), []);
  const closed = seasonStatus === 'finished' || seasonStatus === 'archived';
  const canSubmit = kind === 'game' ? title.trim() !== '' : text.trim() !== '';
  const descriptionField = (
    <TextArea
      label={t.description}
      hint={t.descriptionHint}
      maxLength={2000}
      value={text}
      disabled={pending}
      onChange={(event) => {
        setText(event.target.value);
      }}
    />
  );

  useLayoutEffect(() => {
    const release = (event: FocusEvent) => {
      if (
        focusReturn.current &&
        event.target !== focusReturn.current &&
        event.target !== document.body
      )
        focusReturn.current = null;
    };
    document.addEventListener('focusin', release);
    return () => {
      document.removeEventListener('focusin', release);
    };
  }, []);

  useLayoutEffect(() => {
    if (pending || !focusReturn.current) return;
    const origin = focusReturn.current;
    focusReturn.current = null;
    if (document.activeElement !== origin && document.activeElement !== document.body) return;
    const target =
      origin.isConnected && !origin.matches(':disabled')
        ? origin
        : form.current?.querySelector<HTMLElement>('select');
    target?.focus();
    if (target && target !== origin) target.scrollIntoView({ block: 'center' });
  }, [pending, message]);

  async function submit(event: SyntheticEvent) {
    event.preventDefault();
    if (!canSubmit || closed || pending || write.current) return;
    const parsedHours = hours.trim() === '' ? null : Number(hours);
    const parsedYear = year.trim() === '' ? null : Number(year);
    if (
      kind === 'game' &&
      ((parsedHours !== null &&
        (!Number.isFinite(parsedHours) ||
          parsedHours < 0.5 ||
          parsedHours > 1000 ||
          (parsedHours * 2) % 1 !== 0)) ||
        (parsedYear !== null &&
          (!Number.isInteger(parsedYear) || parsedYear < 1950 || parsedYear > 2100)))
    ) {
      setMessage({ tone: 'danger', text: t.invalidGame });
      return;
    }
    const controller = new AbortController();
    const active = document.activeElement;
    focusReturn.current =
      active instanceof HTMLElement && form.current?.contains(active) ? active : null;
    write.current = controller;
    setPending(true);
    setMessage(null);
    const body: Schemas['SubmitSuggestionRequest'] = {
      commandId: newCommandId(),
      kind,
      text: text.trim(),
      game:
        kind === 'game'
          ? {
              title: title.trim(),
              tags: tags
                .split(',')
                .map((tag) => tag.trim())
                .filter(Boolean),
              hours: parsedHours,
              year: parsedYear,
              steamAppId: steam.trim() || null,
              coverFileId: null,
              note: note.trim() || null,
              isCoop: coop,
              completionCondition: condition.trim() || null,
            }
          : null,
    };
    try {
      const result = await api.POST('/api/seasons/{seasonId}/suggestions', {
        params: { path: { seasonId } },
        body,
        signal: controller.signal,
      });
      if (controller.signal.aborted) return;
      if (result.response.status === 401) onSignedOut();
      else if (result.response.ok) {
        setMessage({ tone: 'success', text: t.sent });
        setText('');
        setTitle('');
        setTags('');
        setHours('');
        setYear('');
        setSteam('');
        setCoop(false);
        setCondition('');
        setNote('');
        loaded.reload();
      } else if (rejectionCode(result.error) === 'feature.disabled') {
        setBlockedRules(rules);
        reloadRules();
      } else {
        const code = rejectionCode(result.error);
        setMessage({ tone: 'danger', text: (code && ru.rejection[code]) || t.failed });
      }
    } catch {
      if (!controller.signal.aborted) setMessage({ tone: 'danger', text: t.failed });
    } finally {
      write.current = null;
      if (!controller.signal.aborted) setPending(false);
    }
  }

  if (blockedRules === rules || (loaded.kind === 'ready' && loaded.value === null)) return null;
  return (
    <section
      aria-labelledby={heading}
      className="grid min-w-0 gap-3 rounded-lg bg-card p-4 desk:p-6"
      data-testid="player-suggestions"
    >
      <h2 id={heading} className="font-display text-lg font-heavy">
        {t.title}
      </h2>
      {closed ? (
        <p className="text-sm text-ink-soft">{t.closed}</p>
      ) : (
        <details className="grid min-w-0 gap-3">
          <Summary className="font-bold">{t.open}</Summary>
          <form
            ref={form}
            onSubmit={(event) => void submit(event)}
            noValidate
            className="grid min-w-0 gap-3 pt-3"
          >
            <p className="max-w-prose text-sm">{t.lead}</p>
            <p className="text-sm text-ink-soft">
              {t.perDay(rules.ruleset.suggestions?.perDay ?? 0)}
            </p>
            <Select
              label={t.kind}
              value={kind}
              disabled={pending}
              onChange={(event) => {
                const next = kinds.find((value) => value === event.target.value);
                if (next) setKind(next);
              }}
            >
              {kinds.map((value) => (
                <option key={value} value={value}>
                  {t.kinds[value]}
                </option>
              ))}
            </Select>
            {kind === 'game' ? (
              <>
                <Field
                  label={t.gameTitle}
                  value={title}
                  maxLength={200}
                  disabled={pending}
                  onChange={(event) => {
                    setTitle(event.target.value);
                  }}
                />
                <Field
                  label={t.tags}
                  value={tags}
                  disabled={pending}
                  onChange={(event) => {
                    setTags(event.target.value);
                  }}
                />
                <details className="grid min-w-0 gap-3">
                  <Summary className="font-medium">{t.moreGame}</Summary>
                  <div className="grid min-w-0 gap-3 pt-3">
                    <Field
                      label={t.hours}
                      hint={t.hoursHint}
                      type="number"
                      min={0.5}
                      max={1000}
                      step={0.5}
                      value={hours}
                      disabled={pending}
                      onChange={(event) => {
                        setHours(event.target.value);
                      }}
                    />
                    <Field
                      label={t.year}
                      type="number"
                      min={1950}
                      max={2100}
                      step={1}
                      value={year}
                      disabled={pending}
                      onChange={(event) => {
                        setYear(event.target.value);
                      }}
                    />
                    <Field
                      label={t.steam}
                      inputMode="numeric"
                      maxLength={12}
                      value={steam}
                      disabled={pending}
                      onChange={(event) => {
                        setSteam(event.target.value);
                      }}
                    />
                    <Checkbox
                      label={t.coop}
                      checked={coop}
                      disabled={pending}
                      onChange={(event) => {
                        setCoop(event.target.checked);
                      }}
                    />
                    <TextArea
                      label={t.condition}
                      value={condition}
                      maxLength={1000}
                      disabled={pending}
                      onChange={(event) => {
                        setCondition(event.target.value);
                      }}
                    />
                    <TextArea
                      label={t.note}
                      value={note}
                      maxLength={1000}
                      disabled={pending}
                      onChange={(event) => {
                        setNote(event.target.value);
                      }}
                    />
                    {descriptionField}
                  </div>
                </details>
              </>
            ) : (
              descriptionField
            )}
            {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
            <Button
              type="submit"
              loading={pending}
              disabled={!canSubmit}
              className="justify-self-start desk:max-w-full desk:whitespace-normal"
            >
              {t.submit}
            </Button>
          </form>
        </details>
      )}
      <h3 className="font-bold">{t.mine}</h3>
      <AsyncState<Schemas['SuggestionView'][] | null> loaded={loaded} errorTitle={t.listFailed}>
        {(items) =>
          !items?.length ? (
            <p className="text-sm text-ink-soft">{t.empty}</p>
          ) : (
            <ul className="grid min-w-0 gap-3">
              {items.map((item) => (
                <li
                  key={item.id}
                  className="grid min-w-0 gap-2 rounded-md border-2 border-muted p-3"
                >
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <h4 className="font-bold wrap-anywhere">
                      {item.game?.title ?? t.kinds[item.kind]}
                    </h4>
                    <Chip
                      tone={
                        item.status === 'approved'
                          ? 'success'
                          : item.status === 'pending'
                            ? 'info'
                            : 'muted'
                      }
                    >
                      {t.statuses[item.status]}
                    </Chip>
                  </div>
                  {item.text ? (
                    <p className="whitespace-pre-wrap wrap-anywhere">{item.text}</p>
                  ) : null}
                  {item.comment ? (
                    <div className="grid gap-1">
                      <p className="text-sm font-bold">{t.adminAnswer}</p>
                      <p className="whitespace-pre-wrap wrap-anywhere">{item.comment}</p>
                    </div>
                  ) : null}
                  <time dateTime={item.submittedAt} className="text-sm text-ink-soft">
                    {moscowTime(item.submittedAt)}
                  </time>
                </li>
              ))}
            </ul>
          )
        }
      </AsyncState>
    </section>
  );
}
