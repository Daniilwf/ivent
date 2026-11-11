import { Crosshair, Plus, X } from 'lucide-react';
import { useMemo, useRef, useState } from 'react';
import { api, type Schemas } from '../../api/client';
import { newCommandId } from '../../api/commands';
import { moscowTime } from '../../app/time';
import { ru } from '../../i18n/ru';
import { Button, IconButton } from '../../ui/Button';
import { ConfirmDanger } from '../../ui/Dialogs';
import { ChoiceGroup, Field, Select, TextArea } from '../../ui/Field';
import { Notice } from '../../ui/States';
import { Panel } from '../../ui/Surface';
import { commentProblem, refusal } from '../actions';
import {
  inspect,
  prefixOf,
  sameContent,
  serverProblems,
  withItem,
  type Collection,
  type ContentSchemas,
  type Pack,
  type Problem,
} from './contentModel';
import { locate } from './jsonLocate';
import { ObjectForm, WheelForm } from './ObjectForm';
import { ObjectPreview } from './ObjectPreview';
import { actionTemplate, actionTypes, type ActionType, type Json, type PackIds } from './templates';

const t = ru.admin.content;

type View = 'form' | 'json';

type ServerAnswer = {
  /** The text the server checked: its problems are shown only while the text is still that */
  text: string;
  own: Problem[];
  other: { path: string; text: string }[];
};

type Outcome = { tone: 'success' | 'danger' | 'info'; text: string } | null;

const pretty = (value: unknown) => JSON.stringify(value, null, 2);

/**
 * One object or wheel of the season's content in the editor (4.12): a form or its JSON, the errors next to their
 * fields, the templates of the base actions, the player's view, and a save that publishes a new version of the whole
 * pack through the queue (D-490). `editable` false — a phone, items off or a closed season: everything can be looked
 * at, checked and copied, nothing saved.
 */
export function ObjectEditor({
  seasonId,
  version,
  pack,
  collection,
  index,
  initial,
  schemas,
  editable,
  readOnly,
  history,
  onSaved,
  onClose,
}: {
  seasonId: string;
  version: number;
  pack: Pack;
  collection: Collection;
  index: number | null;
  initial: Json;
  schemas: ContentSchemas;
  /** Saving is possible: items on, the season open */
  editable: boolean;
  /** A phone: the definition is shown, not edited (like the map editor, D-313) */
  readOnly: boolean;
  history: readonly Schemas['ContentVersionInfo'][];
  onSaved: (outcome: { version: number; id: string | null; text: string }) => void;
  onClose: () => void;
}) {
  const original = useMemo(() => pretty(initial), [initial]);
  const [text, setText] = useState(original);
  const [view, setView] = useState<View>(readOnly ? 'json' : 'form');
  const [server, setServer] = useState<ServerAnswer | null>(null);
  const [comment, setComment] = useState('');
  const [commentError, setCommentError] = useState<string>();
  const [busy, setBusy] = useState<'save' | 'check' | 'remove' | null>(null);
  const [outcome, setOutcome] = useState<Outcome>(null);
  const [removing, setRemoving] = useState(false);
  const [template, setTemplate] = useState<ActionType>('move');
  const editor = useRef<HTMLTextAreaElement>(null);

  const wheel = collection === 'wheels';
  const schema = wheel ? schemas.wheel : schemas.object;
  const checked = useMemo(() => inspect(text, schema), [text, schema]);
  const ids: PackIds = useMemo(
    () => ({
      objects: pack.objects.map((o) => o.id).filter((id): id is string => typeof id === 'string'),
      wheels: pack.wheels.map((w) => w.id).filter((id): id is string => typeof id === 'string'),
    }),
    [pack],
  );
  const value = checked.value;
  const id = typeof value?.id === 'string' ? value.id : null;
  const initialId = typeof initial.id === 'string' ? initial.id : null;
  const taken = id !== null && pack[collection].some((item, i) => i !== index && item.id === id);
  const fresh = server?.text === text ? server : null;
  const problems: Problem[] = [
    ...checked.problems,
    ...(taken && id ? [{ path: 'id', text: t.idTaken(id), source: 'schema' as const }] : []),
    ...(fresh?.own ?? []),
  ];
  // The same content in another order (a field taken out and put back) is not a change: there is nothing to save
  const changed =
    index === null || (value === null ? text !== original : !sameContent(value, initial));
  const blocked = checked.parse !== null || checked.problems.length > 0 || taken;
  const title =
    typeof initial.name === 'string' && initial.name !== '' ? initial.name : t.previewEmpty;
  const place = index ?? pack[collection].length;

  const edit = (next: string) => {
    setText(next);
    setOutcome(null);
  };

  /** Selects the field in the JSON editor: the form view switches to JSON first */
  function goTo(path: string) {
    setView('json');
    requestAnimationFrame(() => {
      const area = editor.current;
      if (!area) return;
      const found = locate(area.value, path);
      area.focus();
      if (found) area.setSelectionRange(found.start, found.end);
    });
  }

  function insert() {
    if (!value) return;
    const effect = (value.effect ?? { target: { selector: 'self' }, actions: [] }) as Json;
    if ('outcomes' in effect && !('actions' in effect)) {
      setOutcome({ tone: 'info', text: t.noActionsList });
      return;
    }
    const actions: unknown[] = Array.isArray(effect.actions) ? (effect.actions as unknown[]) : [];
    edit(
      pretty({
        ...value,
        effect: { ...effect, actions: [...actions, actionTemplate(template, ids)] },
      }),
    );
  }

  async function check() {
    if (!value) return;
    setBusy('check');
    setOutcome(null);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/content/check', {
        params: { path: { seasonId } },
        body: withItem(pack, collection, index, value) as unknown as Schemas['JsonObject'],
      });
      if (answer.data) {
        setServer({ text, ...serverProblems(answer.data.problems, prefixOf(collection, place)) });
        if (answer.data.valid) setOutcome({ tone: 'success', text: t.checked });
      } else setOutcome({ tone: 'danger', text: refusal(answer) });
    } catch {
      setOutcome({ tone: 'danger', text: t.checkFailed });
    } finally {
      setBusy(null);
    }
  }

  async function publish(next: Pack, kind: 'save' | 'remove') {
    const problem = commentProblem(comment);
    setCommentError(problem);
    if (problem) {
      setRemoving(false);
      return;
    }
    setBusy(kind);
    setOutcome(null);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/content/publish', {
        params: { path: { seasonId } },
        body: {
          commandId: newCommandId(),
          expectedVersion: version,
          pack: next,
          comment: comment.trim(),
        },
      });
      if (answer.data) {
        const saved = version + 1;
        onSaved({
          version: saved,
          id: kind === 'remove' ? null : id,
          text: kind === 'remove' ? t.removed(title, saved) : t.saved(saved),
        });
        return;
      }
      const body: unknown = answer.error;
      if (
        answer.response.status === 400 &&
        body &&
        typeof body === 'object' &&
        'errors' in body &&
        Array.isArray(body.errors)
      ) {
        setServer({
          text,
          ...serverProblems(
            body.errors as Schemas['ContentProblemView'][],
            prefixOf(collection, place),
          ),
        });
        setOutcome({ tone: 'danger', text: t.fixFirst });
      } else setOutcome({ tone: 'danger', text: refusal(answer) });
    } catch {
      setOutcome({ tone: 'danger', text: t.failed });
    } finally {
      setBusy(null);
      setRemoving(false);
    }
  }

  const changes = initialId
    ? history.flatMap((v) => {
        const change = v.added.includes(initialId)
          ? 'added'
          : v.changed.includes(initialId)
            ? 'changed'
            : v.removed.includes(initialId)
              ? 'removed'
              : null;
        return change ? [{ version: v, change }] : [];
      })
    : [];

  return (
    <Panel
      data-testid="content-editor"
      aria-labelledby="content-editor-title"
      className="border-2 border-ink"
    >
      <div className="flex items-start gap-3">
        <h2
          id="content-editor-title"
          tabIndex={-1}
          className="mr-auto font-display text-lg font-heavy wrap-anywhere"
        >
          {t.editorTitle(
            index === null ? (t.kindOne[wheel ? 'wheel' : String(initial.kind)] ?? '') : title,
          )}
        </h2>
        <IconButton label={t.close} data-testid="content-editor-close" onClick={onClose}>
          <X size={20} aria-hidden />
        </IconButton>
      </div>

      {readOnly ? null : (
        <ChoiceGroup
          label={t.viewsLabel}
          options={(['form', 'json'] as const).map((v) => ({ value: v, label: t.views[v] ?? v }))}
          value={view}
          onChange={setView}
          data-testid="content-view"
        />
      )}

      {problems.length > 0 || checked.parse ? (
        <div
          role="alert"
          className="grid gap-2 rounded-md bg-danger-soft p-3"
          data-testid="content-problems"
        >
          {checked.parse ? <p className="font-medium">{checked.parse}</p> : null}
          {problems.length > 0 ? (
            <>
              <p className="font-bold">{t.problemsTitle(problems.length)}</p>
              <ul className="grid gap-1">
                {problems.map((p) => (
                  <li
                    key={`${p.source}-${p.path}-${p.text}`}
                    className="flex flex-wrap items-center gap-x-2"
                  >
                    <code className="font-mono text-sm break-all">
                      {p.path === '' ? t.root : p.path}
                    </code>
                    <span className="mr-auto">{p.text}</span>
                    <Button
                      variant="link"
                      icon={<Crosshair size={16} aria-hidden />}
                      aria-label={`${t.goToField}: ${p.path === '' ? t.root : p.path}`}
                      onClick={() => {
                        goTo(p.path);
                      }}
                    >
                      {t.goToField}
                    </Button>
                  </li>
                ))}
              </ul>
            </>
          ) : null}
        </div>
      ) : null}
      {fresh && fresh.other.length > 0 ? (
        <div
          className="grid gap-1 rounded-md bg-warning-soft p-3"
          data-testid="content-other-problems"
        >
          <p className="font-bold">{t.otherProblems}</p>
          <ul className="grid list-disc gap-1 pl-5">
            {fresh.other.map((p) => (
              <li key={`${p.path}-${p.text}`}>
                <code className="font-mono text-sm break-all">{p.path}</code> {p.text}
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {view === 'form' && !readOnly ? (
        value ? (
          wheel ? (
            <WheelForm
              value={value}
              problems={problems}
              ids={ids}
              disabled={false}
              onChange={(next) => {
                edit(pretty(next));
              }}
            />
          ) : (
            <ObjectForm
              value={value}
              problems={problems}
              schemas={schemas}
              ids={ids}
              disabled={false}
              onChange={(next) => {
                edit(pretty(next));
              }}
            />
          )
        ) : (
          <Notice tone="warning">{t.formBroken}</Notice>
        )
      ) : (
        <TextArea
          ref={editor}
          label={wheel ? t.wheelJson : t.json}
          hint={t.jsonHint}
          value={text}
          rows={18}
          readOnly={readOnly}
          spellCheck={false}
          autoCapitalize="off"
          autoCorrect="off"
          className="font-mono"
          data-testid="content-json"
          aria-invalid={blocked || undefined}
          onChange={(e) => {
            edit(e.target.value);
          }}
        />
      )}

      {!wheel && !readOnly ? (
        <div className="flex flex-wrap items-end gap-3" data-testid="content-templates">
          <Select
            label={t.templates}
            hint={t.templatesHint}
            value={template}
            className="min-w-0 grow desk:max-w-110"
            data-testid="content-template"
            onChange={(e) => {
              setTemplate(e.target.value as ActionType);
            }}
          >
            {actionTypes.map((type) => (
              <option key={type} value={type}>
                {`${t.actions[type]?.name ?? type} (${type})`}
              </option>
            ))}
          </Select>
          <Button
            icon={<Plus size={20} aria-hidden />}
            disabled={!value}
            data-testid="content-insert"
            onClick={insert}
          >
            {t.insert}
          </Button>
        </div>
      ) : null}

      {!wheel && value ? (
        <div className="grid gap-2">
          <h3 className="font-bold">{t.preview}</h3>
          <p className="text-sm text-ink-soft">{t.previewHint}</p>
          <ObjectPreview value={value} />
        </div>
      ) : null}

      {outcome ? <Notice tone={outcome.tone}>{outcome.text}</Notice> : null}

      {readOnly ? null : (
        <div className="grid gap-3" data-testid="content-save-panel">
          <Field
            label={t.comment}
            hint={t.commentHint}
            value={comment}
            error={commentError}
            disabled={!editable}
            data-testid="content-comment"
            onChange={(e) => {
              setComment(e.target.value);
              setCommentError(undefined);
            }}
          />
          <div className="flex flex-wrap items-center gap-3">
            <Button
              variant="main"
              loading={busy === 'save'}
              disabled={!editable || !changed || blocked || busy !== null}
              data-testid="content-save"
              onClick={() => {
                if (value) void publish(withItem(pack, collection, index, value), 'save');
              }}
            >
              {t.save}
            </Button>
            <Button
              loading={busy === 'check'}
              disabled={!value || busy !== null}
              data-testid="content-check"
              onClick={() => void check()}
            >
              {t.check}
            </Button>
            {changed && index !== null ? (
              <Button
                variant="link"
                data-testid="content-discard"
                onClick={() => {
                  edit(original);
                  setServer(null);
                }}
              >
                {t.discard}
              </Button>
            ) : null}
            {index !== null ? (
              <ConfirmDanger
                open={removing}
                onOpenChange={setRemoving}
                trigger={
                  <Button
                    variant="danger"
                    disabled={!editable || busy !== null}
                    data-testid="content-remove"
                    className="desk:ml-auto"
                  >
                    {wheel ? t.removeWheel : t.remove}
                  </Button>
                }
                title={t.removeTitle(title)}
                consequences={t.removeConsequences}
                confirm={wheel ? t.removeWheel : t.remove}
                busy={busy === 'remove'}
                testId="content-remove-confirm"
                onConfirm={() => void publish(withItem(pack, collection, index, null), 'remove')}
              />
            ) : null}
          </div>
          {changed && !blocked ? <p className="text-sm text-ink-soft">{t.dirty}</p> : null}
        </div>
      )}

      {initialId ? (
        <div className="grid gap-2" data-testid="content-object-history">
          <h3 className="font-bold">{t.objectHistory}</h3>
          {changes.length === 0 ? (
            <p className="text-ink-soft">{t.objectHistoryEmpty}</p>
          ) : (
            <ul className="grid gap-1">
              {changes.map(({ version: v, change }) => (
                <li key={v.version}>
                  {t.historyRow(v.version, moscowTime(v.at), v.authorName ?? t.system)} —{' '}
                  {t.objectChange[change]}
                  {v.comment ? <span className="text-ink-soft">{` · «${v.comment}»`}</span> : null}
                </li>
              ))}
            </ul>
          )}
        </div>
      ) : null}
    </Panel>
  );
}
