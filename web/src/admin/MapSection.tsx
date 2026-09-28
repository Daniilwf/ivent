import { Plus, Trash2, Undo2 } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { answerOf, useLoaded } from '../app/useLoaded';
import { graphBoard } from '../board/graphBoard';
import { MapLegend } from '../board/MapLegend';
import { MapView } from '../board/MapView';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button, IconButton } from '../ui/Button';
import { ConfirmDanger } from '../ui/Dialogs';
import { Checkbox, Field, Select } from '../ui/Field';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { useDesk } from '../ui/useDesk';
import { commentProblem, refusal } from './actions';
import { MapCanvas } from './MapCanvas';
import {
  addCell,
  addEdge,
  addZone,
  draftOf,
  loadDraft,
  placed,
  moveCell,
  normalized,
  consequenceCodes,
  problemCell,
  removeCell,
  removeEdge,
  removeZone,
  sameMap,
  saveDraft,
  setCell,
  setCellType,
  setDefaultBranch,
  setPrimaryEntry,
  setZone,
  type CellType,
  type Draft,
  type Zone,
} from './mapDraft';

const t = ru.admin.map;

type AdminMap = Schemas['AdminMapView'];
type Check =
  { kind: 'checking' } | { kind: 'failed' } | { kind: 'done'; result: Schemas['MapCheckView'] };

/** How long the editor waits after the last change before it asks the server to check the draft */
const checkDelayMs = 400;

const types: CellType[] = [
  'start',
  'empty',
  'fork',
  'teleport',
  'checkpoint',
  'pointsBonus',
  'finish',
  'event',
  'shop',
];

/** The season's map in the admin (2.11): the editor on a desktop, the published map to look at on a phone */
export function MapSection({ seasonId, version }: { seasonId: string; version: number }) {
  const loaded = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/admin/seasons/{seasonId}/map', { params: { path: { seasonId } } }),
        ),
      [seasonId],
    ),
    { version },
  );
  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {(view, reload) => (
        <MapEditor key={seasonId} seasonId={seasonId} view={view} onPublished={reload} />
      )}
    </AsyncState>
  );
}

function problemText(problem: Schemas['MapProblemView']): string {
  return (
    t.problem[problem.code]?.(problem.subject) ?? t.unknownProblem(problem.code, problem.subject)
  );
}

function MapEditor({
  seasonId,
  view,
  onPublished,
}: {
  seasonId: string;
  view: AdminMap;
  onPublished: () => void;
}) {
  const desk = useDesk();
  const published = useMemo(() => draftOf(view.map), [view.map]);
  // A map without places (a chain from before the graph) gets the places the players' board gives it, once
  const start = useMemo(() => {
    const { board, cellNumber } = graphBoard(published);
    return placed(published, (id) => board.cells.find((c) => c.id === cellNumber.get(id)));
  }, [published]);
  const [kept] = useState(() => loadDraft(seasonId, published));
  const [draft, setDraftState] = useState<Draft>(() => kept.draft ?? start);
  const [selected, setSelected] = useState<string | null>(null);
  // The answer of the check, for the draft (and attempt) it was asked for; another draft is «checking» until its own
  const [answered, setAnswered] = useState<{ key: string; check: Check } | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [outcome, setOutcome] = useState<{ tone: 'success' | 'danger'; text: string } | null>(null);
  const changed = !sameMap(draft, start);

  const setDraft = (next: Draft) => {
    setDraftState(next);
    setOutcome(null);
    saveDraft(seasonId, sameMap(next, start) ? null : { base: published, draft: next });
  };

  // The draft is checked by the server as it changes: every problem at once, the players' cells included
  const body = JSON.stringify(normalized(draft));
  const checkKey = `${attempt}:${body}`;
  const check: Check = answered?.key === checkKey ? answered.check : { kind: 'checking' };
  useEffect(() => {
    let active = true;
    const setCheck = (next: Check) => {
      setAnswered({ key: checkKey, check: next });
    };
    const timer = window.setTimeout(() => {
      api
        .POST('/api/admin/seasons/{seasonId}/map/check', {
          params: { path: { seasonId } },
          body: JSON.parse(body) as Schemas['MapGraphView'],
        })
        .then(({ data }) => {
          if (active) setCheck(data ? { kind: 'done', result: data } : { kind: 'failed' });
        })
        .catch(() => {
          if (active) setCheck({ kind: 'failed' });
        });
    }, checkDelayMs);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [body, seasonId, checkKey, view]);

  // Only the causes are outlined red on the canvas: a cut-off cell is not where the fix is
  const problemCells = new Set(
    (check.kind === 'done' ? check.result.problems : [])
      .filter((p) => !consequenceCodes.has(p.code))
      .map((p) => problemCell(draft, p.subject))
      .filter((c): c is string => c !== null),
  );
  const onCell = useMemo(() => {
    const names = new Map<string, string[]>();
    for (const p of view.players) names.set(p.cellId, [...(names.get(p.cellId) ?? []), p.name]);
    return names;
  }, [view.players]);
  const counts = useMemo(
    () => new Map([...onCell].map(([cell, names]) => [cell, names.length])),
    [onCell],
  );

  const notices = (
    <>
      {view.mode === 'linear' ? <Notice tone="info">{t.linear}</Notice> : null}
      {kept.stale ? <Notice tone="warning">{t.draftStale}</Notice> : null}
      {view.status !== 'draft' && view.status !== 'active' ? (
        <Notice tone="warning">{t.closed}</Notice>
      ) : null}
    </>
  );

  // A phone shows the published map to look at: dragging cells needs a pointer and room (D-313)
  if (!desk) return <PhoneView view={view} notices={notices} />;

  const cell = draft.cells.find((c) => c.id === selected) ?? null;
  return (
    <div className="grid gap-6" data-testid="map-editor">
      {notices}
      <div className="grid items-start gap-4">
        <div className="grid min-w-0 gap-2">
          <MapCanvas
            draft={draft}
            selected={selected}
            problems={problemCells}
            players={counts}
            onSelect={setSelected}
            onMove={(id, at) => {
              setDraft(moveCell(draft, id, at));
            }}
            onConnect={(from, to) => {
              setDraft(addEdge(draft, from, to));
            }}
          />
          <p className="text-sm text-ink-soft">{t.canvasHint}</p>
        </div>
        <div className="grid items-start gap-4 desk:grid-cols-2">
          <Panel>
            <div className="flex flex-wrap gap-3">
              <Button
                icon={<Plus size={20} aria-hidden />}
                data-testid="map-add-cell"
                onClick={() => {
                  const right = Math.max(0, ...draft.cells.map((c) => c.x ?? 0));
                  const added = addCell(draft, { x: right + 140, y: 0 });
                  setDraft(added.draft);
                  setSelected(added.id);
                }}
              >
                {t.addCell}
              </Button>
              {changed ? (
                <Button
                  variant="link"
                  icon={<Undo2 size={20} aria-hidden />}
                  data-testid="map-reset"
                  onClick={() => {
                    setDraft(start);
                    setSelected(null);
                  }}
                >
                  {t.reset}
                </Button>
              ) : null}
            </div>
            {changed ? (
              <p className="text-sm text-ink-soft" data-testid="map-changed">
                {t.draftChanged}
              </p>
            ) : null}
            <Select
              label={t.cellsList}
              value={selected ?? ''}
              data-testid="map-cell-pick"
              onChange={(e) => {
                setSelected(e.target.value || null);
              }}
            >
              <option value="">{t.cell.pick}</option>
              {draft.cells.map((c) => (
                <option key={c.id} value={c.id}>
                  {`${c.id} (${t.types[c.type] ?? c.type})`}
                </option>
              ))}
            </Select>
            {cell ? (
              <CellPanel
                key={cell.id}
                draft={draft}
                id={cell.id}
                players={onCell.get(cell.id) ?? []}
                onChange={setDraft}
                onRemoved={() => {
                  setSelected(null);
                }}
              />
            ) : (
              <p className="text-sm text-ink-soft">{t.noSelection}</p>
            )}
          </Panel>
          <ZonesPanel draft={draft} onChange={setDraft} />
        </div>
      </div>
      <CheckPanel
        check={check}
        draft={draft}
        same={!changed}
        onRetry={() => {
          setAttempt((n) => n + 1);
        }}
        onSelect={setSelected}
      />
      <PublishPanel
        seasonId={seasonId}
        draft={draft}
        canPublish={check.kind === 'done' && check.result.canPublish}
        reason={
          check.kind === 'checking'
            ? t.check.checking
            : check.kind === 'failed'
              ? t.check.failed
              : check.result.problems.length > 0
                ? t.publish.fixFirst
                : view.mode === 'linear'
                  ? t.publish.linearFirst
                  : view.status !== 'draft' && view.status !== 'active'
                    ? t.closed
                    : check.result.canPublish
                      ? null
                      : t.publish.nothingNew
        }
        outcome={outcome}
        onOutcome={setOutcome}
        onPublished={() => {
          saveDraft(seasonId, null);
          setOutcome({ tone: 'success', text: t.publish.done });
          onPublished();
        }}
      />
    </div>
  );
}

function PhoneView({ view, notices }: { view: AdminMap; notices: React.ReactNode }) {
  const map = useMemo(() => draftOf(view.map), [view.map]);
  const { board, cellNumber } = useMemo(() => graphBoard(map), [map]);
  return (
    <div className="grid gap-4" data-testid="map-phone">
      <Notice tone="info">{t.phone}</Notice>
      {notices}
      <MapView
        board={board}
        players={view.players.map((p, i) => ({
          id: p.playerId,
          name: p.name,
          token: i,
          cell: cellNumber.get(p.cellId) ?? 0,
          points: 0,
        }))}
        tools="top"
        className="h-105 rounded-lg border-3 border-ink"
      />
      <MapLegend zones={map.zones} kinds={new Set(map.cells.map((c) => c.type))} />
    </div>
  );
}

function CellPanel({
  draft,
  id,
  players,
  onChange,
  onRemoved,
}: {
  draft: Draft;
  id: string;
  players: string[];
  onChange: (draft: Draft) => void;
  onRemoved: () => void;
}) {
  const cell = draft.cells.find((c) => c.id === id);
  const [target, setTarget] = useState('');
  const [amount, setAmount] = useState(String(cell?.amount ?? ''));
  if (!cell) return null;
  const exits = draft.edges.filter((e) => e.from === id);
  const entries = draft.edges.filter((e) => e.to === id);
  const others = draft.cells.filter((c) => c.id !== id);
  return (
    <section
      className="grid gap-3 border-t-2 border-muted pt-3"
      data-testid="map-cell"
      aria-labelledby="map-cell-title"
    >
      <h3 id="map-cell-title" className="font-display font-heavy">
        {t.cell.title(cell.id)}
      </h3>
      {players.length > 0 ? (
        <p className="text-sm font-bold text-me">{t.cell.players(players.join(', '))}</p>
      ) : null}
      <Select
        label={t.cell.type}
        value={cell.type}
        data-testid="map-cell-type"
        onChange={(e) => {
          onChange(setCellType(draft, id, e.target.value as CellType));
        }}
      >
        {types.map((type) => (
          <option key={type} value={type}>
            {t.types[type] ?? type}
          </option>
        ))}
      </Select>
      <Select
        label={t.cell.zone}
        value={cell.zone ?? ''}
        data-testid="map-cell-zone"
        onChange={(e) => {
          onChange(setCell(draft, id, { zone: e.target.value || null }));
        }}
      >
        <option value="">{t.cell.noZone}</option>
        {draft.zones.map((z) => (
          <option key={z.id} value={z.id}>
            {z.name || z.id}
          </option>
        ))}
      </Select>
      {cell.type === 'teleport' ? (
        <Select
          label={t.cell.to}
          value={cell.to ?? ''}
          data-testid="map-cell-to"
          onChange={(e) => {
            onChange(setCell(draft, id, { to: e.target.value || null }));
          }}
        >
          <option value="">{t.cell.pick}</option>
          {others.map((c) => (
            <option key={c.id} value={c.id}>
              {c.id}
            </option>
          ))}
        </Select>
      ) : null}
      {cell.type === 'pointsBonus' ? (
        <Field
          label={t.cell.amount}
          hint={t.cell.amountHint}
          inputMode="numeric"
          value={amount}
          data-testid="map-cell-amount"
          onChange={(e) => {
            setAmount(e.target.value);
            const n = Number(e.target.value.replace('−', '-'));
            if (Number.isInteger(n) && e.target.value.trim() !== '')
              onChange(setCell(draft, id, { amount: n }));
          }}
        />
      ) : null}

      <div className="grid gap-1">
        <h4 className="text-sm font-bold">{t.cell.exits}</h4>
        {exits.length === 0 ? <p className="text-sm text-ink-soft">{t.cell.noExits}</p> : null}
        <ul className="grid gap-1">
          {exits.map((e) => (
            <li key={e.to} className="flex items-center gap-2" data-testid={`map-exit-${e.to}`}>
              <span className="min-w-0 grow wrap-anywhere">{t.edge.to(e.to)}</span>
              {exits.length > 1 ? (
                <Checkbox
                  label={t.edge.defaultTag}
                  checked={e.isDefaultForward}
                  onChange={() => {
                    onChange(setDefaultBranch(draft, id, e.to));
                  }}
                />
              ) : null}
              <IconButton
                label={t.edge.remove(id, e.to)}
                onClick={() => {
                  onChange(removeEdge(draft, id, e.to));
                }}
              >
                <Trash2 size={18} />
              </IconButton>
            </li>
          ))}
        </ul>
      </div>
      <div className="grid gap-1">
        <h4 className="text-sm font-bold">{t.cell.entries}</h4>
        {entries.length === 0 ? <p className="text-sm text-ink-soft">{t.cell.noEntries}</p> : null}
        <ul className="grid gap-1">
          {entries.map((e) => (
            <li
              key={e.from}
              className="flex items-center gap-2"
              data-testid={`map-entry-${e.from}`}
            >
              <span className="min-w-0 grow wrap-anywhere">{t.edge.from(e.from)}</span>
              {entries.length > 1 ? (
                <Checkbox
                  label={t.edge.primaryTag}
                  checked={e.isPrimaryBackward}
                  onChange={() => {
                    onChange(setPrimaryEntry(draft, e.from, id));
                  }}
                />
              ) : null}
            </li>
          ))}
        </ul>
      </div>
      <div className="grid grid-cols-[minmax(0,1fr)_auto] items-end gap-2">
        <Select
          label={t.cell.arrowTo}
          value={target}
          data-testid="map-arrow-to"
          onChange={(e) => {
            setTarget(e.target.value);
          }}
        >
          <option value="">{t.cell.pick}</option>
          {others
            .filter((c) => !exits.some((e) => e.to === c.id))
            .map((c) => (
              <option key={c.id} value={c.id}>
                {c.id}
              </option>
            ))}
        </Select>
        <Button
          disabled={target === ''}
          data-testid="map-arrow-add"
          onClick={() => {
            onChange(addEdge(draft, id, target));
            setTarget('');
          }}
        >
          {t.cell.addArrow}
        </Button>
      </div>
      <Button
        variant="dangerLink"
        className="justify-self-start"
        data-testid="map-cell-remove"
        onClick={() => {
          onChange(removeCell(draft, id));
          onRemoved();
        }}
      >
        {t.cell.remove}
      </Button>
    </section>
  );
}

const numberOrNull = (text: string) => {
  const n = Number(text.replace(',', '.').trim());
  return text.trim() === '' || !Number.isFinite(n) ? null : n;
};

function ZonesPanel({ draft, onChange }: { draft: Draft; onChange: (draft: Draft) => void }) {
  return (
    <Panel title={t.zone.title} data-testid="map-zones">
      {draft.zones.length === 0 ? <p className="text-sm text-ink-soft">{t.zone.empty}</p> : null}
      {draft.zones.map((zone) => (
        <ZoneForm key={zone.id} zone={zone} draft={draft} onChange={onChange} />
      ))}
      <Button
        icon={<Plus size={20} aria-hidden />}
        className="justify-self-start"
        data-testid="map-zone-add"
        onClick={() => {
          onChange(addZone(draft, t.zone.newName(draft.zones.length + 1)).draft);
        }}
      >
        {t.zone.add}
      </Button>
    </Panel>
  );
}

function ZoneForm({
  zone,
  draft,
  onChange,
}: {
  zone: Zone;
  draft: Draft;
  onChange: (draft: Draft) => void;
}) {
  // Numbers are typed as text: «1,5» and a half-typed «1,» must survive until they mean something
  const [tags, setTags] = useState((zone.rollFilter?.tags ?? []).join(', '));
  const [minHours, setMinHours] = useState(String(zone.rollFilter?.minHours ?? ''));
  const [maxHours, setMaxHours] = useState(String(zone.rollFilter?.maxHours ?? ''));
  const [drop, setDrop] = useState(
    zone.dropPenaltyMultiplier == null ? '' : String(zone.dropPenaltyMultiplier),
  );
  const [dice, setDice] = useState(String(zone.diceModifier?.value ?? 1));
  const filter = (patch: Partial<NonNullable<Zone['rollFilter']>>) =>
    setZone(draft, zone.id, { rollFilter: { ...(zone.rollFilter ?? {}), ...patch } });
  const stage = zone.diceModifier?.stage ?? '';
  return (
    <fieldset
      className="grid gap-3 border-t-2 border-muted pt-3"
      data-testid={`map-zone-${zone.id}`}
    >
      <legend className="sr-only">{zone.name}</legend>
      <Field
        label={t.zone.name}
        value={zone.name}
        onChange={(e) => {
          onChange(setZone(draft, zone.id, { name: e.target.value }));
        }}
      />
      <Field
        label={t.zone.tags}
        hint={t.zone.tagsHint}
        value={tags}
        onChange={(e) => {
          setTags(e.target.value);
          onChange(
            filter({
              tags: e.target.value
                .split(',')
                .map((tag) => tag.trim())
                .filter(Boolean),
            }),
          );
        }}
      />
      <div className="grid grid-cols-2 gap-3">
        <Field
          label={t.zone.minHours}
          inputMode="decimal"
          value={minHours}
          onChange={(e) => {
            setMinHours(e.target.value);
            onChange(filter({ minHours: numberOrNull(e.target.value) }));
          }}
        />
        <Field
          label={t.zone.maxHours}
          inputMode="decimal"
          value={maxHours}
          onChange={(e) => {
            setMaxHours(e.target.value);
            onChange(filter({ maxHours: numberOrNull(e.target.value) }));
          }}
        />
      </div>
      <div className="grid grid-cols-[minmax(0,1fr)_auto] items-end gap-3">
        <Select
          label={t.zone.dice}
          value={stage}
          onChange={(e) => {
            const next = e.target.value;
            onChange(
              setZone(draft, zone.id, {
                diceModifier:
                  next === 'count' || next === 'add'
                    ? { stage: next, value: Number.parseInt(dice, 10) || 1 }
                    : null,
              }),
            );
          }}
        >
          <option value="">{t.zone.diceNone}</option>
          <option value="count">{t.zone.diceCount}</option>
          <option value="add">{t.zone.diceAdd}</option>
        </Select>
        {stage ? (
          <Field
            label={t.zone.diceValue}
            inputMode="numeric"
            className="w-24"
            value={dice}
            onChange={(e) => {
              setDice(e.target.value);
              const n = Number(e.target.value.replace('−', '-'));
              if (Number.isInteger(n) && zone.diceModifier)
                onChange(
                  setZone(draft, zone.id, { diceModifier: { ...zone.diceModifier, value: n } }),
                );
            }}
          />
        ) : null}
      </div>
      <Field
        label={t.zone.drop}
        hint={t.zone.dropHint}
        inputMode="decimal"
        value={drop}
        onChange={(e) => {
          setDrop(e.target.value);
          onChange(
            setZone(draft, zone.id, { dropPenaltyMultiplier: numberOrNull(e.target.value) }),
          );
        }}
      />
      <Button
        variant="dangerLink"
        className="justify-self-start"
        onClick={() => {
          onChange(removeZone(draft, zone.id));
        }}
      >
        {t.zone.remove}
      </Button>
    </fieldset>
  );
}

/** The check of the draft in words: the problems (a cell's problem selects the cell), the zone warnings */
export function CheckPanel({
  check,
  draft,
  onRetry,
  onSelect,
  same = false,
}: {
  check: Check;
  draft: Draft;
  onRetry: () => void;
  onSelect: (cell: string) => void;
  /** The draft is the published map */
  same?: boolean;
}) {
  const causes =
    check.kind === 'done' ? check.result.problems.filter((p) => !consequenceCodes.has(p.code)) : [];
  const zoneName = (id: string) => draft.zones.find((z) => z.id === id)?.name ?? id;
  return (
    <Panel title={t.check.title} data-testid="map-check" aria-live="polite">
      {check.kind === 'checking' ? (
        <p className="text-ink-soft">{t.check.checking}</p>
      ) : check.kind === 'failed' ? (
        <div className="grid justify-items-start gap-2">
          <Notice tone="danger">{t.check.failed}</Notice>
          <Button onClick={onRetry}>{t.check.retry}</Button>
        </div>
      ) : (
        <>
          {check.result.problems.length === 0 ? (
            <Notice tone={check.result.canPublish ? 'success' : 'info'}>
              {check.result.canPublish ? t.check.ok : same ? t.check.unchanged : t.check.noProblems}
            </Notice>
          ) : (
            <div className="grid gap-2" data-testid="map-problems">
              <p className="font-bold text-danger">{t.check.problems(causes.length)}</p>
              <ul className="grid list-disc gap-1 pl-5">
                {causes.map((p) => {
                  const cell = problemCell(draft, p.subject);
                  return (
                    <li key={`${p.code}-${p.subject}`}>
                      {cell ? (
                        <button
                          type="button"
                          className="cursor-pointer rounded-sm text-left underline decoration-2 underline-offset-4 is-focus:focus-ring"
                          onClick={() => {
                            onSelect(cell);
                          }}
                        >
                          {problemText(p)}
                        </button>
                      ) : (
                        problemText(p)
                      )}
                    </li>
                  );
                })}
              </ul>
              {[...consequenceCodes].map((code) => {
                const cells = check.result.problems
                  .filter((p) => p.code === code)
                  .map((p) => p.subject);
                return cells.length === 0 ? null : (
                  <p key={code} className="text-sm text-ink-soft" data-testid={`map-cut-${code}`}>
                    {t.check.consequence(code, cells)}
                  </p>
                );
              })}
            </div>
          )}
          {check.result.warnings.map((w) => (
            <Notice key={w.zoneId} tone="warning">
              {t.check.warning(zoneName(w.zoneId), w.available, w.wanted)}
            </Notice>
          ))}
        </>
      )}
    </Panel>
  );
}

function PublishPanel({
  seasonId,
  draft,
  canPublish,
  reason,
  outcome,
  onOutcome,
  onPublished,
}: {
  seasonId: string;
  draft: Draft;
  canPublish: boolean;
  /** Why the button is disabled, said under it */
  reason: string | null;
  outcome: { tone: 'success' | 'danger'; text: string } | null;
  onOutcome: (outcome: { tone: 'success' | 'danger'; text: string } | null) => void;
  onPublished: () => void;
}) {
  const [comment, setComment] = useState('');
  const [error, setError] = useState<string>();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);

  async function publish() {
    setBusy(true);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/map/publish', {
        params: { path: { seasonId } },
        body: { commandId: newCommandId(), map: normalized(draft), comment: comment.trim() },
      });
      if (answer.data) {
        setComment('');
        onPublished();
      } else onOutcome({ tone: 'danger', text: refusal(answer) });
    } catch {
      onOutcome({ tone: 'danger', text: ru.admin.failed });
    } finally {
      setBusy(false);
      setOpen(false);
    }
  }

  return (
    <Panel title={t.publish.title} data-testid="map-publish">
      {outcome ? <Notice tone={outcome.tone}>{outcome.text}</Notice> : null}
      <Field
        label={t.publish.comment}
        hint={t.publish.commentHint}
        value={comment}
        error={error}
        data-testid="map-publish-comment"
        onChange={(e) => {
          setComment(e.target.value);
          setError(undefined);
        }}
      />
      <div>
        <ConfirmDanger
          open={open}
          onOpenChange={(next) => {
            if (next) {
              const problem = commentProblem(comment);
              if (problem) {
                setError(problem);
                return;
              }
            }
            setOpen(next);
          }}
          trigger={
            <Button variant="main" disabled={!canPublish} data-testid="map-publish-button">
              {t.publish.button}
            </Button>
          }
          title={t.publish.confirmTitle}
          consequences={t.publish.consequences}
          confirm={t.publish.confirm}
          busy={busy}
          testId="map-publish-confirm"
          onConfirm={() => void publish()}
        />
      </div>
      {canPublish || !reason ? null : (
        <p className="text-sm text-ink-soft" data-testid="map-publish-reason">
          {reason}
        </p>
      )}
    </Panel>
  );
}
