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
import { BetsPanel } from './Bets';
import { ShopPanel } from './Shop';
import {
  clockOffset,
  refusalText,
  type Economy,
  type Loaded,
  type PanelState,
  type UseResult,
} from './economy';

export type { Economy, Loaded, PanelState, UseResult } from './economy';
export type InventoryItem = Schemas['InventoryItemView'];

/** What the player answers in the sheet: the target (when the item asks for one) and the steps' answers in order */
export type ItemUse = { targetPlayerId: string | null; choices: string[] };

async function fetchEconomy(seasonId: string): Promise<Loaded> {
  try {
    const { data, response } = await api.GET('/api/seasons/{seasonId}/economy', {
      params: { path: { seasonId } },
    });
    if (data) {
      const economy: Economy = data;
      return {
        kind: 'ready',
        economy,
        clockOffset: clockOffset(economy.serverNow, response.headers.get('date')),
      };
    }
    return response.status === 401 ? { kind: 'signedOut' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * My economy on the season screen (4.11a, 4.11c, D-441, D-447): one load of `GET …/economy` for the inventory, the shop
 * (when `shop`) and the bets (when `bets`); again after every command of the season (`version` — the season's last
 * sequence, the hub moves it), so an item another player used on me or a lot gone by the timer shows at once. Each
 * panel only when its mechanic is on: the bets (or the shop) may play without items, the inventory is then not shown.
 */
export function EconomySection({
  seasonId,
  version,
  onSignedOut,
  items = true,
  shop = false,
  bets = false,
  className,
}: {
  seasonId: string;
  version: number;
  onSignedOut: () => void;
  /** Items on: the inventory panel; the shop or the bets may play without items */
  items?: boolean;
  shop?: boolean;
  bets?: boolean;
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

  // One command of the economy: done (and the economy loads again), or refused in words of the dictionary
  async function act(
    send: () => Promise<{ response: Response; error?: unknown }>,
    own?: Record<string, string>,
  ): Promise<UseResult> {
    try {
      const result = await send();
      if (result.response.status === 401) {
        onSignedOut();
        return { ok: false, message: ru.rejection.unknown };
      }
      if (result.error)
        return { ok: false, message: refusalText(rejectionCode(result.error), own) };
      setRetry((n) => n + 1);
      return { ok: true };
    } catch {
      return { ok: false, message: ru.app.loadError };
    }
  }

  const path = { params: { path: { seasonId } } };
  const state: PanelState = loaded.kind === 'signedOut' ? { kind: 'loading' } : loaded;
  const onRetry = () => {
    setLoaded({ kind: 'loading' });
    setRetry((n) => n + 1);
  };

  return (
    <>
      {items ? (
        <InventoryPanel
          state={state}
          onRetry={onRetry}
          onUse={(item, answer) =>
            act(() =>
              api.POST('/api/seasons/{seasonId}/items/{instanceId}/use', {
                params: { path: { seasonId, instanceId: item.instanceId } },
                body: {
                  commandId: newCommandId(),
                  targetPlayerId: answer.targetPlayerId,
                  choices: answer.choices,
                },
              }),
            )
          }
          className={className}
        />
      ) : null}
      {shop ? (
        <ShopPanel
          state={state}
          onRetry={onRetry}
          onRoll={() =>
            act(
              () =>
                api.POST('/api/seasons/{seasonId}/shop/roll', {
                  ...path,
                  body: { commandId: newCommandId() },
                }),
              ru.shop.refusals,
            )
          }
          onBuy={(lot) =>
            act(
              () =>
                api.POST('/api/seasons/{seasonId}/shop/buy', {
                  ...path,
                  body: { commandId: newCommandId(), lot },
                }),
              ru.shop.refusals,
            )
          }
          className={className}
        />
      ) : null}
      {bets ? (
        <BetsPanel
          state={state}
          onRetry={onRetry}
          onBet={(bet) =>
            act(
              () =>
                api.POST('/api/seasons/{seasonId}/bets', {
                  ...path,
                  body: { commandId: newCommandId(), ...bet },
                }),
              ru.bets.refusals,
            )
          }
          className={className}
        />
      ) : null}
    </>
  );
}

/** The panel in each of its states; the styleguide shows them with fixtures */
export function InventoryPanel({
  state,
  onRetry,
  onUse,
  className,
}: {
  state: PanelState;
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
      className="grid gap-2 border-t-2 border-muted pt-3 first:border-t-0 first:pt-0"
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
          {!isItem ? <Chip tone="info">{ru.inventory.actsAloneChip(item.kind)}</Chip> : null}
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
              : refusalText(item.refusal)}
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
  // Each step's answers: one slot, or one per object of a pick of several (D-476)
  const [answers, setAnswers] = useState<(string | null)[][]>(item.steps.map(() => []));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const targetName = useId();

  const optionsOf = (step: Schemas['ItemStepView']) =>
    step.kind === 'pickObject'
      ? (step.objectsByTarget?.find((t) => t.playerId === (target ?? ''))?.objects ??
        (item.choosesTarget ? [] : (step.objectsByTarget?.[0]?.objects ?? [])))
      : step.options;
  // An object to pick from a target who has none asks no answer: the engine takes nothing and reads none (D-440)
  const asked = (step: Schemas['ItemStepView']) =>
    step.kind !== 'pickObject' ||
    (item.choosesTarget && target === null) ||
    optionsOf(step).length > 0;
  // A pick asks `count` different objects (all when fewer); a count known only at the use asks a list of preference,
  // each place optional: the rest is taken at random
  const slotsOf = (step: Schemas['ItemStepView']) =>
    step.kind !== 'pickObject'
      ? 1
      : Math.max(
          1,
          step.ranked ? optionsOf(step).length : Math.min(step.count, optionsOf(step).length),
        );
  // The answers of a step that still fit the target picked, in order, each once
  const given = (step: Schemas['ItemStepView'], i: number) => {
    const ids = new Set(optionsOf(step).map((o) => o.id));
    const picked = (answers[i] ?? []).slice(0, slotsOf(step));
    return picked.filter((a, j): a is string => a != null && ids.has(a) && picked.indexOf(a) === j);
  };
  const ready =
    (!item.choosesTarget || target !== null) &&
    item.steps.every(
      (step, i) =>
        !asked(step) ||
        (step.kind === 'pickObject' && step.ranked) ||
        given(step, i).length === slotsOf(step),
    );
  const answer = (i: number, j: number, value: string | null) => {
    setAnswers((all) =>
      all.map((slots, k) => {
        if (k !== i) return slots;
        const next = [...slots];
        next[j] = value;
        return next;
      }),
    );
    setError(null);
  };
  const labelOf = (step: Schemas['ItemStepView'], j: number) => {
    if (step.kind === 'direction') return ru.inventory.direction;
    if (step.kind !== 'pickObject') return step.prompt ?? ru.inventory.pick;
    if (step.ranked) return ru.inventory.preference(j + 1);
    return slotsOf(step) > 1 ? ru.inventory.pickObjectNumbered(j + 1) : ru.inventory.pickObject;
  };
  const optionLabel = (step: Schemas['ItemStepView'], o: Schemas['ItemOptionView']) =>
    step.kind === 'direction'
      ? o.id === 'back'
        ? ru.inventory.back
        : o.id === 'forward'
          ? ru.inventory.forward
          : o.label
      : o.label;

  return (
    <FormDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) onClose();
      }}
      title={ru.inventory.useTitle(item.name)}
      testId="use-item"
      {...(returnFocus ? { returnFocus } : {})}
    >
      <p className="max-w-prose wrap-anywhere">{item.description}</p>
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (!ready || busy) return;
          setBusy(true);
          setError(null);
          void onUse({
            targetPlayerId: item.choosesTarget ? target : null,
            choices: item.steps.flatMap((step, i) => (asked(step) ? given(step, i) : [])),
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
          const slots = Array.from({ length: slotsOf(step) }, (_, j) => j);
          return (
            <div key={i} className="grid gap-1">
              {step.kind === 'pickObject' && step.ranked && asked(step) ? (
                <span className="text-sm text-ink-soft">{ru.inventory.preferenceHint}</span>
              ) : null}
              {slots.map((j) => {
                const value = answers[i]?.[j] ?? null;
                // A pick of several never offers the same object twice
                const taken = new Set((answers[i] ?? []).filter((a, k) => k !== j && a != null));
                return (
                  <Select
                    key={j}
                    label={labelOf(step, j)}
                    data-testid={slots.length > 1 ? `use-step-${i}-${j}` : `use-step-${i}`}
                    value={value ?? ''}
                    disabled={
                      options.length === 0 ||
                      (step.kind === 'pickObject' && item.choosesTarget && !target)
                    }
                    onChange={(e) => {
                      answer(i, j, e.target.value || null);
                    }}
                  >
                    <option value="">{ru.inventory.pick}</option>
                    {options
                      .filter((o) => !taken.has(o.id))
                      .map((o) => (
                        <option key={o.id} value={o.id}>
                          {optionLabel(step, o)}
                        </option>
                      ))}
                  </Select>
                );
              })}
              {step.kind === 'pickObject' && !asked(step) ? (
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
