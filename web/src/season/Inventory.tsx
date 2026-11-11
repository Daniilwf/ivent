import { Backpack, Clock, Swords, UserRound } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { Select } from '../ui/Field';
import { Badge, Chip } from '../ui/Marks';
import { Skeleton } from '../ui/Progress';
import { EmptyState, ErrorState, Notice } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { Panel } from '../ui/Surface';
import { cx } from '../ui/cx';

export type Economy = Schemas['EconomyView'];
export type InventoryItem = Schemas['InventoryItemView'];

/** What the player answers in the sheet: the target (when the item asks for one) and the steps' answers in order */
export type ItemUse = { targetPlayerId: string | null; choices: string[] };

/** The answer of the server to a use: done, or refused with the engine's code */
export type UseResult = { ok: true } | { ok: false; message: string };

type Loaded =
  | { kind: 'loading' }
  | { kind: 'failed' }
  | { kind: 'signedOut' }
  | { kind: 'ready'; economy: Economy };

async function fetchEconomy(seasonId: string): Promise<Loaded> {
  try {
    const { data, response } = await api.GET('/api/seasons/{seasonId}/economy', {
      params: { path: { seasonId } },
    });
    if (data) return { kind: 'ready', economy: data };
    return response.status === 401 ? { kind: 'signedOut' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * My inventory on the season screen (4.11a, D-441): loads with the page and again after every command of the season
 * (`version` — the season's last sequence, the hub moves it), so an item another player used on me shows at once.
 * Hidden by the page when items are off.
 */
export function InventorySection({
  seasonId,
  version,
  onSignedOut,
  className,
}: {
  seasonId: string;
  version: number;
  onSignedOut: () => void;
  className?: string;
}) {
  const [loaded, setLoaded] = useState<Loaded>({ kind: 'loading' });
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let active = true;
    void fetchEconomy(seasonId).then((next) => {
      if (!active) return;
      if (next.kind === 'signedOut') onSignedOut();
      // A refresh that failed keeps what was shown: the list does not blink away on a lost connection
      else setLoaded((shown) => (next.kind === 'failed' && shown.kind === 'ready' ? shown : next));
    });
    return () => {
      active = false;
    };
  }, [seasonId, version, retry, onSignedOut]);

  async function use(item: InventoryItem, answer: ItemUse): Promise<UseResult> {
    try {
      const result = await api.POST('/api/seasons/{seasonId}/items/{instanceId}/use', {
        params: { path: { seasonId, instanceId: item.instanceId } },
        body: {
          commandId: newCommandId(),
          targetPlayerId: answer.targetPlayerId,
          choices: answer.choices,
        },
      });
      if (result.response.status === 401) {
        onSignedOut();
        return { ok: false, message: ru.rejection.unknown };
      }
      if (result.error) {
        const code = rejectionCode(result.error);
        return { ok: false, message: (code && ru.rejection[code]) ?? ru.rejection.unknown };
      }
      setRetry((n) => n + 1);
      return { ok: true };
    } catch {
      return { ok: false, message: ru.app.loadError };
    }
  }

  return (
    <InventoryPanel
      state={loaded.kind === 'signedOut' ? { kind: 'loading' } : loaded}
      onRetry={() => {
        setLoaded({ kind: 'loading' });
        setRetry((n) => n + 1);
      }}
      onUse={use}
      className={className}
    />
  );
}

/** The panel in each of its states; the styleguide shows them with fixtures */
export function InventoryPanel({
  state,
  onRetry,
  onUse,
  className,
}: {
  state: Exclude<Loaded, { kind: 'signedOut' }>;
  onRetry: () => void;
  onUse: (item: InventoryItem, answer: ItemUse) => Promise<UseResult>;
  className?: string | undefined;
}) {
  const [using, setUsing] = useState<InventoryItem | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const opener = useRef<HTMLButtonElement | null>(null);
  const economy = state.kind === 'ready' ? state.economy : null;
  const items = economy?.items ?? [];
  const held = items.filter((i) => i.kind === 'item').length;

  return (
    <Panel
      aria-labelledby="inventory-title"
      data-testid="inventory"
      className={cx('min-w-0', className)}
    >
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <h2 id="inventory-title" className="font-display text-lg font-heavy">
          {ru.inventory.title}
        </h2>
        {economy ? (
          <span className="text-sm text-ink-soft tabular-nums" data-testid="inventory-count">
            {ru.inventory.count(held, economy.inventoryLimit)}
          </span>
        ) : null}
      </div>
      {economy ? (
        <p className="font-bold tabular-nums" data-testid="coins">
          {ru.inventory.coins(economy.coins)}
        </p>
      ) : null}
      <p className="sr-only" aria-live="polite" data-testid="inventory-announce">
        {done ?? ''}
      </p>
      {done ? <Notice tone="success">{done}</Notice> : null}
      {state.kind === 'loading' ? (
        <div className="grid gap-3" aria-busy="true">
          <p className="sr-only">{ru.ui.loading}</p>
          <Skeleton className="h-5 w-1/3" />
          <Skeleton className="h-20 w-full" />
          <Skeleton className="h-20 w-full" />
        </div>
      ) : state.kind === 'failed' ? (
        <ErrorState
          title={ru.inventory.loadErrorTitle}
          text={ru.inventory.loadError}
          onRetry={onRetry}
        />
      ) : items.length === 0 ? (
        <EmptyState
          icon={<Backpack size={28} aria-hidden />}
          title={ru.inventory.emptyTitle}
          text={ru.inventory.emptyText}
        />
      ) : (
        <ul className="grid gap-3">
          {items.map((item) => (
            <ItemRow
              key={item.instanceId}
              item={item}
              onUse={(button) => {
                opener.current = button;
                setDone(null);
                setUsing(item);
              }}
            />
          ))}
        </ul>
      )}
      {using ? (
        <UseItemDialog
          item={using}
          returnFocus={opener}
          onClose={() => {
            setUsing(null);
          }}
          onUse={async (answer) => {
            const result = await onUse(using, answer);
            if (result.ok) {
              setUsing(null);
              setDone(ru.inventory.used(using.name));
            }
            return result;
          }}
        />
      ) : null}
    </Panel>
  );
}

/** One object: what it is, what it does, how long it lasts; «Использовать» only in its moment */
export function ItemRow({
  item,
  onUse,
  force,
}: {
  item: InventoryItem;
  onUse: (button: HTMLButtonElement) => void;
  /** Styleguide only: a state of the button */
  force?: string;
}) {
  const titleId = useId();
  const isItem = item.kind === 'item';
  const facts = [
    item.usesLeft != null ? ru.inventory.usesLeft(item.usesLeft) : null,
    item.runsLeft != null ? ru.inventory.runsLeft(item.runsLeft) : null,
  ].filter((f): f is string => f !== null);
  return (
    <li
      aria-labelledby={titleId}
      data-testid={`item-${item.objectId}`}
      className="grid gap-2 rounded-md border-2 border-muted p-3"
    >
      <div className="flex flex-wrap items-center gap-2">
        <h3 id={titleId} className="font-display font-heavy wrap-anywhere">
          {item.name}
        </h3>
        {item.rarity && item.rarity !== 'common' ? (
          <Badge tone={item.rarity === 'legendary' ? 'gold' : 'muted'}>
            {ru.inventory.rarity[item.rarity]}
          </Badge>
        ) : null}
        {item.hostile ? (
          <Badge tone="danger" icon={<Swords size={12} aria-hidden />}>
            {ru.inventory.hostile}
          </Badge>
        ) : null}
      </div>
      <p className="max-w-prose text-sm wrap-anywhere">{item.description}</p>
      {facts.length > 0 || item.expiresAt || item.fromPlayer || !isItem ? (
        <div className="flex flex-wrap gap-2">
          {!isItem ? (
            <Chip tone="info">{`${ru.inventory.kinds[item.kind]}: ${ru.inventory.actsAlone.toLowerCase()}`}</Chip>
          ) : null}
          {facts.map((f) => (
            <Chip key={f}>{f}</Chip>
          ))}
          {item.expiresAt ? (
            <Chip icon={<Clock size={16} aria-hidden />}>
              <span className="tabular-nums">{ru.inventory.until(moscowTime(item.expiresAt))}</span>
            </Chip>
          ) : null}
          {item.fromPlayer ? (
            <Chip icon={<UserRound size={16} aria-hidden />}>
              {ru.inventory.from(item.fromPlayer)}
            </Chip>
          ) : null}
        </div>
      ) : null}
      {isItem ? (
        item.usable ? (
          <Button
            className="justify-self-start"
            data-testid={`use-${item.objectId}`}
            {...(force ? { force } : {})}
            aria-describedby={titleId}
            onClick={(e) => {
              onUse(e.currentTarget);
            }}
          >
            {ru.inventory.use}
          </Button>
        ) : (
          <p className="text-sm text-ink-soft" data-testid={`why-${item.objectId}`}>
            {item.refusal === 'item.wrongWindow' && item.window
              ? ru.inventory.windows[item.window]
              : ((item.refusal && ru.rejection[item.refusal]) ?? ru.rejection.unknown)}
          </p>
        )
      ) : null}
    </li>
  );
}

/**
 * The sheet of «Использовать» (D-440): the target among the players the engine accepts, then each answer the item asks,
 * then the press. A refusal of the server stays in the sheet with the choices made, in words of the dictionary.
 */
export function UseItemDialog({
  item,
  onUse,
  onClose,
  returnFocus,
  open = true,
}: {
  item: InventoryItem;
  onUse: (answer: ItemUse) => Promise<UseResult>;
  onClose: () => void;
  returnFocus?: React.RefObject<HTMLElement | null>;
  open?: boolean;
}) {
  const [target, setTarget] = useState<string | null>(
    item.choosesTarget && item.targets.length === 1 ? (item.targets[0]?.playerId ?? null) : null,
  );
  const [answers, setAnswers] = useState<(string | null)[]>(item.steps.map(() => null));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const targetName = useId();

  const optionsOf = (step: Schemas['ItemStepView']) =>
    step.kind === 'pickObject'
      ? (step.objectsByTarget?.find((t) => t.playerId === (target ?? ''))?.objects ??
        (item.choosesTarget ? [] : (step.objectsByTarget?.[0]?.objects ?? [])))
      : step.options;
  const ready =
    (!item.choosesTarget || target !== null) &&
    item.steps.every((step, i) => answers[i] && optionsOf(step).some((o) => o.id === answers[i]));

  return (
    <FormDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) onClose();
      }}
      title={ru.inventory.useTitle(item.name)}
      description={[item.description]}
      testId="use-item"
      {...(returnFocus ? { returnFocus } : {})}
    >
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (!ready || busy) return;
          setBusy(true);
          setError(null);
          void onUse({
            targetPlayerId: item.choosesTarget ? target : null,
            choices: answers.filter((a): a is string => a !== null),
          }).then((result) => {
            setBusy(false);
            if (!result.ok) setError(result.message);
          });
        }}
      >
        {item.choosesTarget ? (
          <fieldset className="grid min-w-0 gap-2" aria-describedby={`${targetName}-hint`}>
            <legend className="mb-1 text-sm font-bold">{ru.inventory.target}</legend>
            <span id={`${targetName}-hint`} className="text-sm text-ink-soft">
              {ru.inventory.targetHint}
            </span>
            <div className="grid gap-2" data-testid="use-targets">
              {item.targets.map((t) => {
                const picked = target === t.playerId;
                return (
                  <label
                    key={t.playerId}
                    data-testid={`target-${t.playerId}`}
                    className={cx(
                      'flex min-h-12 cursor-pointer items-center gap-3 rounded-md border-2 border-ink px-3 font-medium transition duration-(--duration-fast) select-none has-[:focus-visible]:focus-ring',
                      picked ? 'bg-ink text-card' : 'bg-card text-ink is-hover:bg-page',
                    )}
                  >
                    <input
                      type="radio"
                      name={targetName}
                      value={t.playerId}
                      checked={picked}
                      onChange={() => {
                        setTarget(t.playerId);
                        setError(null);
                      }}
                      className="sr-only"
                    />
                    <Sticker player={{ name: t.name, token: t.token }} size={28} />
                    <span className="min-w-0 wrap-anywhere">{t.name}</span>
                  </label>
                );
              })}
            </div>
          </fieldset>
        ) : null}
        {item.steps.map((step, i) => {
          const options = optionsOf(step);
          const label =
            step.kind === 'pickObject'
              ? ru.inventory.pickObject
              : (step.prompt ?? ru.inventory.pick);
          return (
            <div key={i} className="grid gap-1">
              <Select
                label={label}
                data-testid={`use-step-${i}`}
                value={answers[i] ?? ''}
                disabled={
                  options.length === 0 ||
                  (step.kind === 'pickObject' && item.choosesTarget && !target)
                }
                onChange={(e) => {
                  const value = e.target.value;
                  setAnswers((all) => all.map((a, j) => (j === i ? value || null : a)));
                  setError(null);
                }}
              >
                <option value="">{ru.inventory.pick}</option>
                {options.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.label}
                  </option>
                ))}
              </Select>
              {step.kind === 'pickObject' && target && options.length === 0 ? (
                <span className="text-sm text-ink-soft">{ru.inventory.noObjects}</span>
              ) : null}
            </div>
          );
        })}
        {error ? <Notice tone="danger">{error}</Notice> : null}
        <Button
          type="submit"
          variant="main"
          data-testid="use-item-confirm"
          disabled={!ready}
          loading={busy}
        >
          {ru.inventory.use}
        </Button>
      </form>
    </FormDialog>
  );
}
