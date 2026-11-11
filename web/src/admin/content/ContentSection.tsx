import { Download, Plus } from 'lucide-react';
import { useCallback, useMemo, useRef, useState } from 'react';
import { api, type Schemas } from '../../api/client';
import { newCommandId } from '../../api/commands';
import { answerOf, useLoaded } from '../../app/useLoaded';
import { moscowTime } from '../../app/time';
import { ru } from '../../i18n/ru';
import { AsyncState } from '../../ui/AsyncState';
import { Button } from '../../ui/Button';
import { ConfirmDanger } from '../../ui/Dialogs';
import { ChoiceGroup, Field, FilePicker, Select } from '../../ui/Field';
import { Badge } from '../../ui/Marks';
import { Notice } from '../../ui/States';
import { Panel } from '../../ui/Surface';
import { useDesk } from '../../ui/useDesk';
import { commentProblem, refusal } from '../actions';
import { isSchema } from '../ruleSchema';
import {
  contentSchemas,
  download,
  maxPackBytes,
  packDiff,
  packOf,
  serverText,
  type Collection,
  type ContentSchemas,
  type Pack,
} from './contentModel';
import { ObjectEditor } from './ObjectEditor';
import { newKinds, newObject, type Json, type NewKind } from './templates';

const t = ru.admin.content;

type View = Schemas['AdminContentView'];

type Filter = 'item' | 'effect' | 'specialRoll' | 'wheels' | 'deleted';

const filters: Filter[] = ['item', 'effect', 'specialRoll', 'wheels', 'deleted'];

type Open = { collection: Collection; index: number | null; initial: Json; key: number };

/** The season's content in the admin (4.12): the objects and wheels in force, an editor for one, import, export, versions */
export function ContentSection({ seasonId, version }: { seasonId: string; version: number }) {
  const loaded = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/admin/seasons/{seasonId}/content', {
            params: { path: { seasonId } },
          }),
        ),
      [seasonId],
    ),
    { version },
  );
  const schema = useLoaded(
    useCallback(async () => {
      const { data } = await api.GET('/api/admin/content/schema');
      return isSchema(data) ? { kind: 'ready' as const, value: data } : { kind: 'failed' as const };
    }, []),
  );
  // The last save's word outlives the reload it causes
  const [saved, setSaved] = useState<{ text: string; open: string | null } | null>(null);

  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {(view, reload) => (
        <Workspace
          key={`${seasonId}-${view.version}`}
          seasonId={seasonId}
          view={view}
          schemas={contentSchemas(schema.kind === 'ready' ? schema.value : null)}
          schemaFailed={schema.kind === 'failed'}
          saved={saved}
          onSaved={(next) => {
            setSaved(next);
            reload();
          }}
        />
      )}
    </AsyncState>
  );
}

function Workspace({
  seasonId,
  view,
  schemas,
  schemaFailed,
  saved,
  onSaved,
}: {
  seasonId: string;
  view: View;
  schemas: ContentSchemas;
  schemaFailed: boolean;
  saved: { text: string; open: string | null } | null;
  onSaved: (next: { text: string; open: string | null }) => void;
}) {
  const desk = useDesk();
  const pack = useMemo(() => packOf(view.pack), [view.pack]);
  const open = view.status === 'draft' || view.status === 'active';
  const editable = desk && view.itemsEnabled && open;
  const [filter, setFilter] = useState<Filter>('item');
  const [kind, setKind] = useState<NewKind>('item');
  const counter = useRef(0);
  // After a save the saved object stays open, at its place in the new version
  const [editing, setEditing] = useState<Open | null>(() => {
    if (!saved?.open) return null;
    const index = pack.objects.findIndex((o) => o.id === saved.open);
    if (index >= 0)
      return { collection: 'objects', index, initial: pack.objects[index] ?? {}, key: 0 };
    const wheel = pack.wheels.findIndex((w) => w.id === saved.open);
    return wheel >= 0
      ? { collection: 'wheels', index: wheel, initial: pack.wheels[wheel] ?? {}, key: 0 }
      : null;
  });
  const ids = {
    objects: pack.objects.map((o) => String(o.id)),
    wheels: pack.wheels.map((w) => String(w.id)),
  };

  const openItem = (collection: Collection, index: number | null, initial: Json) => {
    counter.current += 1;
    setEditing({ collection, index, initial, key: counter.current });
    requestAnimationFrame(() => {
      const heading = document.getElementById('content-editor-title');
      heading?.focus();
      heading?.scrollIntoView({ block: 'start' });
    });
  };

  const rows =
    filter === 'wheels'
      ? pack.wheels.map((w, index) => ({ item: w, index, collection: 'wheels' as const }))
      : filter === 'deleted'
        ? []
        : pack.objects
            .map((o, index) => ({ item: o, index, collection: 'objects' as const }))
            .filter((r) => r.item.kind === filter);

  return (
    <div className="grid gap-6" data-testid="admin-content">
      {saved ? <Notice tone="success">{saved.text}</Notice> : null}
      {view.itemsEnabled ? null : <Notice tone="info">{t.itemsOff}</Notice>}
      {open ? null : <Notice tone="warning">{t.closed}</Notice>}
      {desk ? null : <Notice tone="info">{t.phone}</Notice>}
      {schemaFailed ? <Notice tone="info">{t.schemaFailed}</Notice> : null}

      <Panel title={t.version(view.version)} data-testid="content-list">
        <ChoiceGroup
          label={t.kindsLabel}
          options={filters.map((f) => ({
            value: f,
            label: `${t.kinds[f] ?? f} · ${
              f === 'wheels'
                ? pack.wheels.length
                : f === 'deleted'
                  ? view.deleted.length
                  : pack.objects.filter((o) => o.kind === f).length
            }`,
          }))}
          value={filter}
          onChange={setFilter}
          data-testid="content-filter"
        />
        {view.version === 0 && pack.objects.length === 0 ? (
          <p className="text-ink-soft">{t.empty}</p>
        ) : null}
        {filter === 'deleted' ? (
          view.deleted.length === 0 ? (
            <p className="text-ink-soft">{t.emptyKind}</p>
          ) : (
            <ul className="grid gap-2" data-testid="content-deleted">
              {view.deleted.map((d) => (
                <li
                  key={d.id}
                  className="flex flex-wrap items-center gap-x-3 gap-y-1 wrap-anywhere"
                >
                  <span className="font-bold">{d.name}</span>
                  <code className="font-mono text-sm text-ink-soft">{d.id}</code>
                  <Badge tone="muted">{t.kindOne[d.kind] ?? d.kind}</Badge>
                  <span className="text-sm text-ink-soft">{t.deletedIn(d.deletedIn)}</span>
                </li>
              ))}
            </ul>
          )
        ) : rows.length === 0 ? (
          <p className="text-ink-soft">{t.emptyKind}</p>
        ) : (
          <ul className="grid gap-1" data-testid="content-rows">
            {rows.map(({ item, index, collection }) => {
              const name = typeof item.name === 'string' ? item.name : '';
              const id = typeof item.id === 'string' ? item.id : '';
              const current = editing?.collection === collection && editing.index === index;
              return (
                <li
                  key={`${collection}-${id}-${index}`}
                  className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-md px-2 py-1 wrap-anywhere is-hover:bg-page"
                  data-testid={`content-row-${id}`}
                >
                  <span className="font-bold">{name === '' ? t.previewEmpty : name}</span>
                  <code className="mr-auto font-mono text-sm text-ink-soft">{id}</code>
                  {collection === 'wheels' ? (
                    <span className="text-sm text-ink-soft">
                      {t.entries(Array.isArray(item.entries) ? item.entries.length : 0)}
                    </span>
                  ) : (
                    <>
                      {typeof item.rarity === 'string' ? (
                        <Badge tone="muted">{t.values.rarity?.[item.rarity] ?? item.rarity}</Badge>
                      ) : null}
                      {item.kind === 'item' ? (
                        <span className="text-sm text-ink-soft">
                          {typeof item.price === 'number' ? t.price(item.price) : t.notForSale}
                        </span>
                      ) : null}
                    </>
                  )}
                  {current ? (
                    <Badge tone="ink">{t.opened}</Badge>
                  ) : (
                    <Button
                      variant="link"
                      aria-label={t.openNamed(name === '' ? id : name)}
                      data-testid={`content-open-${id}`}
                      onClick={() => {
                        openItem(collection, index, item);
                      }}
                    >
                      {t.open}
                    </Button>
                  )}
                </li>
              );
            })}
          </ul>
        )}
        <div className="flex flex-wrap items-end gap-3 border-t-2 border-muted pt-3">
          {desk ? (
            <>
              <Select
                label={t.createKind}
                value={kind}
                data-testid="content-new-kind"
                onChange={(e) => {
                  setKind(e.target.value as NewKind);
                }}
              >
                {newKinds.map((k) => (
                  <option key={k} value={k}>
                    {t.kindOne[k] ?? k}
                  </option>
                ))}
              </Select>
              <Button
                icon={<Plus size={20} aria-hidden />}
                data-testid="content-new"
                onClick={() => {
                  openItem(
                    kind === 'wheel' ? 'wheels' : 'objects',
                    null,
                    newObject(kind, ids, {
                      name: t.newName,
                      description: t.newDescription,
                      wheel: t.newWheelName,
                    }),
                  );
                }}
              >
                {t.create}
              </Button>
            </>
          ) : null}
          <Button
            icon={<Download size={20} aria-hidden />}
            disabled={view.version === 0}
            data-testid="content-export"
            className="desk:ml-auto"
            onClick={() => {
              download(`content-v${view.version}.json`, pack);
            }}
          >
            {t.export}
          </Button>
        </div>
      </Panel>

      {editing ? (
        <ObjectEditor
          key={editing.key}
          seasonId={seasonId}
          version={view.version}
          pack={pack}
          collection={editing.collection}
          index={editing.index}
          initial={editing.initial}
          schemas={schemas}
          editable={editable}
          readOnly={!desk}
          history={view.history}
          onSaved={(next) => {
            onSaved({ text: next.text, open: next.id });
          }}
          onClose={() => {
            setEditing(null);
          }}
        />
      ) : null}

      {desk ? (
        <Import seasonId={seasonId} view={view} pack={pack} editable={editable} onSaved={onSaved} />
      ) : null}

      <History seasonId={seasonId} view={view} />
    </div>
  );
}

type Imported = {
  name: string;
  pack: Pack;
  check:
    | { kind: 'checking' }
    | { kind: 'done'; result: Schemas['ContentCheckView'] }
    | { kind: 'failed'; text: string };
};

/** A pack from a file (D-498): read in the browser, checked by the server, published whole as a new version */
function Import({
  seasonId,
  view,
  pack,
  editable,
  onSaved,
}: {
  seasonId: string;
  view: View;
  pack: Pack;
  editable: boolean;
  onSaved: (next: { text: string; open: string | null }) => void;
}) {
  const [imported, setImported] = useState<Imported | null>(null);
  const [error, setError] = useState<string>();
  const [comment, setComment] = useState('');
  const [commentError, setCommentError] = useState<string>();
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<string>();

  async function read(file: File) {
    setError(undefined);
    setOutcome(undefined);
    setImported(null);
    if (file.size > maxPackBytes) {
      setError(t.importTooBig);
      return;
    }
    let value: unknown;
    try {
      value = JSON.parse(await file.text());
    } catch (e) {
      setError(t.importNotJson(e instanceof Error ? e.message : ''));
      return;
    }
    if (!isSchema(value) || !Array.isArray((value as Json).objects)) {
      setError(t.importNotPack);
      return;
    }
    const next = { name: file.name, pack: packOf(value) };
    setImported({ ...next, check: { kind: 'checking' } });
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/content/check', {
        params: { path: { seasonId } },
        body: value as Schemas['JsonObject'],
      });
      setImported({
        ...next,
        check: answer.data
          ? { kind: 'done', result: answer.data }
          : { kind: 'failed', text: refusal(answer) },
      });
    } catch {
      setImported({ ...next, check: { kind: 'failed', text: t.checkFailed } });
    }
  }

  async function publish() {
    if (!imported) return;
    setBusy(true);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/content/publish', {
        params: { path: { seasonId } },
        body: {
          commandId: newCommandId(),
          expectedVersion: view.version,
          pack: imported.pack,
          comment: comment.trim(),
        },
      });
      if (answer.data) {
        onSaved({ text: t.saved(view.version + 1), open: null });
        return;
      }
      setOutcome(refusal(answer));
    } catch {
      setOutcome(t.failed);
    } finally {
      setBusy(false);
      setConfirm(false);
    }
  }

  const diff = imported ? packDiff(pack, imported.pack) : null;
  const valid = imported?.check.kind === 'done' && imported.check.result.valid;

  return (
    <Panel title={t.importPick} data-testid="content-import">
      <FilePicker
        label={t.importPick}
        hint={t.importHint}
        accept="application/json,.json"
        data-testid="content-import-file"
        onChange={(e) => {
          const file = e.target.files?.[0];
          if (file) void read(file);
          e.target.value = '';
        }}
      />
      {error ? <Notice tone="danger">{error}</Notice> : null}
      {imported && diff ? (
        <div className="grid gap-3" data-testid="content-import-result">
          <p className="font-bold wrap-anywhere">{t.importTitle(imported.name)}</p>
          <p>
            {t.importSummary(imported.pack.objects.length, imported.pack.wheels.length)}{' '}
            {t.importDiff(diff.added, diff.changed, diff.removed)}
          </p>
          {imported.check.kind === 'checking' ? (
            <p className="text-ink-soft">{t.importChecking}</p>
          ) : null}
          {imported.check.kind === 'failed' ? (
            <Notice tone="danger">{imported.check.text}</Notice>
          ) : null}
          {imported.check.kind === 'done' ? (
            imported.check.result.valid ? (
              <Notice tone="success">{t.importValid}</Notice>
            ) : (
              <div
                role="alert"
                className="grid gap-1 rounded-md bg-danger-soft p-3"
                data-testid="content-import-problems"
              >
                <ul className="grid list-disc gap-1 pl-5">
                  {imported.check.result.problems.map((p) => (
                    <li key={`${p.path}-${p.message}`} className="wrap-anywhere">
                      <code className="font-mono text-sm">{p.path}</code> {serverText(p.message)}
                    </li>
                  ))}
                </ul>
              </div>
            )
          ) : null}
          {outcome ? <Notice tone="danger">{outcome}</Notice> : null}
          <Field
            label={t.comment}
            hint={t.commentHint}
            value={comment}
            error={commentError}
            disabled={!editable}
            data-testid="content-import-comment"
            onChange={(e) => {
              setComment(e.target.value);
              setCommentError(undefined);
            }}
          />
          <div className="flex flex-wrap gap-3">
            <ConfirmDanger
              open={confirm}
              onOpenChange={(next) => {
                const problem = next ? commentProblem(comment) : undefined;
                setCommentError(problem);
                setConfirm(next && !problem);
              }}
              trigger={
                <Button
                  variant="main"
                  disabled={!editable || !valid || busy}
                  data-testid="content-import-publish"
                >
                  {t.importPublish}
                </Button>
              }
              title={t.importConfirmTitle}
              consequences={t.importConsequences}
              confirm={t.importPublish}
              busy={busy}
              testId="content-import-confirm"
              onConfirm={() => void publish()}
            />
            <Button
              variant="link"
              onClick={() => {
                setImported(null);
                setOutcome(undefined);
              }}
            >
              {t.importCancel}
            </Button>
          </div>
        </div>
      ) : null}
    </Panel>
  );
}

/** The versions of the season's content, newest first, each with what it changed and a file to download */
function History({ seasonId, view }: { seasonId: string; view: View }) {
  const [busy, setBusy] = useState<number | null>(null);
  const [error, setError] = useState<string>();

  async function exportVersion(version: number) {
    setBusy(version);
    setError(undefined);
    try {
      const answer = await api.GET('/api/admin/seasons/{seasonId}/content/versions/{version}', {
        params: { path: { seasonId, version } },
      });
      if (answer.data) download(`content-v${version}.json`, answer.data.pack);
      else setError(refusal(answer));
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  return (
    <Panel title={t.history} data-testid="content-history">
      {error ? <Notice tone="danger">{error}</Notice> : null}
      {view.history.length === 0 ? (
        <p className="text-ink-soft">{t.historyEmpty}</p>
      ) : (
        <ol className="grid gap-3">
          {view.history.map((v) => (
            <li
              key={v.version}
              className="grid gap-1 border-b-2 border-muted pb-3 last:border-b-0 wrap-anywhere"
            >
              <p className="font-bold">
                {t.historyRow(v.version, moscowTime(v.at), v.authorName ?? t.system)}
              </p>
              <p>«{v.comment}»</p>
              <p className="text-sm text-ink-soft">
                {t.historyCounts(v.objects, v.wheels)} ·{' '}
                {t.historyChanges(v.added.length, v.changed.length, v.removed.length)}
              </p>
              <div>
                <Button
                  variant="link"
                  icon={<Download size={16} aria-hidden />}
                  loading={busy === v.version}
                  data-testid={`content-export-${v.version}`}
                  onClick={() => void exportVersion(v.version)}
                >
                  {t.exportVersion(v.version)}
                </Button>
              </div>
            </li>
          ))}
        </ol>
      )}
    </Panel>
  );
}
