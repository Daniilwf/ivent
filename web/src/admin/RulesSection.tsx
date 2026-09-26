import { useCallback, useMemo, useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ConfirmDanger } from '../ui/Dialogs';
import { TextArea } from '../ui/Field';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { newCommandId, refusal } from './actions';
import { Loading } from './common';
import { answerOf, useLoaded } from '../app/useLoaded';
import { checkSchema, isSchema, type JsonSchema, type SchemaProblem } from './ruleSchema';

const t = ru.admin.rules;

type Rules = Schemas['RulesView'];

const shownProblems = 10;

function describe(problem: SchemaProblem): string {
  switch (problem.kind) {
    case 'required':
      return t.required(problem.path);
    case 'unknown':
      return t.unknownField(problem.path);
    case 'type':
      return t.wrongType(problem.path, problem.expected.map((x) => t.types[x] ?? x).join(t.or));
    case 'enum':
      return t.notInList(problem.path, problem.values.map((v) => JSON.stringify(v)).join(', '));
  }
}

/** What the typed text says: not JSON, not the schema, or a ruleset to send */
function inspect(text: string, schema: JsonSchema | null) {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch (e) {
    return { parse: t.parseError(e instanceof Error ? e.message : ''), problems: [], value: null };
  }
  return { parse: null, problems: schema ? checkSchema(value, schema) : [], value };
}

/** The rules of the season as JSON, checked by the schema, saved with the server's warnings (D-113) */
export function RulesSection({ seasonId, version }: { seasonId: string; version: number }) {
  const loaded = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/seasons/{seasonId}/rules', { params: { path: { seasonId } } }),
        ),
      [seasonId],
    ),
    { version },
  );
  const schema = useLoaded(
    useCallback(async () => {
      const { data } = await api.GET('/api/admin/rules/schema');
      return isSchema(data) ? { kind: 'ready' as const, value: data } : { kind: 'failed' as const };
    }, []),
  );
  // The outcome of the last save outlives the editor, which is rebuilt for the new version
  const [outcome, setOutcome] = useState<Outcome>(null);

  return (
    <Loading loaded={loaded}>
      {(rules, reload) => (
        <RulesEditor
          key={`${seasonId}-${rules.version}`}
          seasonId={seasonId}
          rules={rules}
          schema={schema.kind === 'ready' ? schema.value : null}
          schemaFailed={schema.kind === 'failed'}
          outcome={outcome}
          onSaved={(next) => {
            setOutcome(next);
            reload();
          }}
        />
      )}
    </Loading>
  );
}

type Outcome = { saved: number; warnings: readonly string[] } | null;

function RulesEditor({
  seasonId,
  rules,
  schema,
  schemaFailed,
  outcome,
  onSaved,
}: {
  seasonId: string;
  rules: Rules;
  schema: JsonSchema | null;
  schemaFailed: boolean;
  outcome: Outcome;
  onSaved: (outcome: NonNullable<Outcome>) => void;
}) {
  const original = useMemo(() => JSON.stringify(rules.ruleset, null, 2), [rules.ruleset]);
  const [text, setText] = useState(original);
  const [busy, setBusy] = useState(false);
  const [serverErrors, setServerErrors] = useState<string[]>([]);
  const [error, setError] = useState<string>();
  const checked = useMemo(() => inspect(text, schema), [text, schema]);
  const invalid = checked.parse !== null || checked.problems.length > 0;

  async function save() {
    if (invalid) return;
    setBusy(true);
    setError(undefined);
    setServerErrors([]);
    try {
      const answer = await api.PUT('/api/admin/seasons/{seasonId}/rules', {
        params: { path: { seasonId } },
        body: {
          commandId: newCommandId(),
          expectedVersion: rules.version,
          ruleset: checked.value as Schemas['Ruleset'],
        },
      });
      if (answer.data) {
        onSaved({ saved: answer.data.version, warnings: answer.data.warnings });
        return;
      }
      const problem: unknown = answer.error;
      if (
        answer.response.status === 400 &&
        problem &&
        typeof problem === 'object' &&
        'errors' in problem &&
        Array.isArray(problem.errors)
      ) {
        setServerErrors(
          (problem.errors as Schemas['RulesetError'][]).map((e) => `${e.path}: ${e.message}`),
        );
      } else setError(refusal(answer));
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  const bonusesKept = outcome?.warnings.includes('finish.bonusesKept') ?? false;

  return (
    <div className="grid gap-6" data-testid="admin-rules">
      {outcome ? (
        <div className="grid gap-2" data-testid="rules-outcome">
          <Notice tone="success">{t.saved(outcome.saved)}</Notice>
          {outcome.warnings.map((w) => (
            <Notice key={w} tone="warning">
              {t.warnings[w] ?? t.unknownWarning(w)}
            </Notice>
          ))}
        </div>
      ) : null}
      <Panel>
        <p className="font-bold" data-testid="rules-version">
          {t.version(rules.version)}
        </p>
        {schemaFailed ? <Notice tone="info">{t.schemaFailed}</Notice> : null}
        <TextArea
          label={t.editor}
          hint={t.editorHint}
          value={text}
          rows={20}
          spellCheck={false}
          autoCapitalize="off"
          autoCorrect="off"
          data-testid="rules-editor"
          aria-invalid={invalid || undefined}
          onChange={(e) => {
            setText(e.target.value);
          }}
        />
        {checked.parse ? (
          <p role="alert" className="text-sm font-medium text-danger" data-testid="rules-parse">
            {checked.parse}
          </p>
        ) : null}
        {checked.problems.length > 0 ? (
          <div role="alert" className="grid gap-1 text-sm text-danger" data-testid="rules-problems">
            <p className="font-bold">{t.schemaErrors}</p>
            <ul className="grid list-disc gap-1 pl-5 break-all">
              {checked.problems.slice(0, shownProblems).map((p) => (
                <li key={`${p.kind}-${p.path}`}>{describe(p)}</li>
              ))}
            </ul>
          </div>
        ) : null}
        {serverErrors.length > 0 ? (
          <div role="alert" className="grid gap-1 text-sm text-danger" data-testid="rules-server">
            <p className="font-bold">{t.serverErrors}</p>
            <ul className="grid list-disc gap-1 pl-5 break-all">
              {serverErrors.map((e) => (
                <li key={e}>{e}</li>
              ))}
            </ul>
          </div>
        ) : null}
        {error ? <Notice tone="danger">{error}</Notice> : null}
        <div className="flex flex-wrap items-center gap-3">
          <Button
            variant="main"
            loading={busy}
            disabled={text === original}
            data-testid="rules-save"
            onClick={() => void save()}
          >
            {t.save}
          </Button>
          {text === original ? null : (
            <Button
              variant="link"
              data-testid="rules-reset"
              onClick={() => {
                setText(original);
                setServerErrors([]);
              }}
            >
              {t.reset}
            </Button>
          )}
        </div>
      </Panel>
      <Recalculate seasonId={seasonId} highlighted={bonusesKept} />
      <History rules={rules} />
    </div>
  );
}

/** «Пересчитать бонусы по текущим правилам» (D-113): the finishers move to the bonus list now in force */
function Recalculate({ seasonId, highlighted }: { seasonId: string; highlighted: boolean }) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ tone: 'success' | 'info' | 'danger'; text: string }>();

  async function recalculate() {
    setBusy(true);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/finish-bonuses/recalculate', {
        params: { path: { seasonId } },
        body: { commandId: newCommandId() },
      });
      if (answer.data) setResult({ tone: 'success', text: t.recalculated });
      else
        setResult({
          tone: rejectionCode(answer.error) === 'finish.nothingToRecalculate' ? 'info' : 'danger',
          text: refusal(answer),
        });
    } catch {
      setResult({ tone: 'danger', text: ru.admin.failed });
    } finally {
      setOpen(false);
      setBusy(false);
    }
  }

  return (
    <Panel
      title={t.bonuses}
      className={highlighted ? 'border-2 border-warning' : undefined}
      data-testid="recalculate-panel"
    >
      <p className="max-w-prose text-ink-soft">{t.recalcLead}</p>
      {result ? <Notice tone={result.tone}>{result.text}</Notice> : null}
      <div>
        <ConfirmDanger
          open={open}
          onOpenChange={setOpen}
          trigger={
            <Button data-testid="recalculate" className="text-center whitespace-normal!">
              {t.recalc}
            </Button>
          }
          title={t.recalcTitle}
          consequences={t.recalcConsequences}
          confirm={t.recalcConfirm}
          busy={busy}
          onConfirm={() => void recalculate()}
        />
      </div>
    </Panel>
  );
}

function History({ rules }: { rules: Rules }) {
  const versions = [...rules.history].sort((a, b) => b.version - a.version);
  return (
    <Panel title={t.history} data-testid="rules-history">
      <ol className="grid gap-3">
        {versions.map((v) => (
          <li key={v.version} className="grid gap-1">
            <p className="font-bold">{t.historyEntry(v.version, moscowTime(v.at))}</p>
            {v.changes.length === 0 ? (
              <p className="text-sm text-ink-soft">{t.created}</p>
            ) : (
              <ul className="grid gap-1 text-sm break-all">
                {v.changes.map((c) => (
                  <li key={c.path}>{t.change(c.path, c.before, c.after)}</li>
                ))}
              </ul>
            )}
          </li>
        ))}
      </ol>
    </Panel>
  );
}
