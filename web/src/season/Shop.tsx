import { Check, Clock, Store, Ticket } from 'lucide-react';
import { useId, useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Badge, Chip } from '../ui/Marks';
import { Skeleton } from '../ui/Progress';
import { EmptyState, ErrorState, Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { cx } from '../ui/cx';
import { refusalText, timeLeft, useNow, type PanelState, type UseResult } from './economy';

export type ShopLot = Schemas['ShopLotView'];

/**
 * My shop (4.11c, D-443, D-447): the roll — by a coupon first, then for coins, dearer each time within a game — and the
 * personal lots with their timer. «Купить за N» is open only on a lot the engine sells me now; otherwise its reason in words.
 * Lots past their time vanish by the clock even before the server's timer fires (it refuses them anyway). Hidden when
 * the season's shop is off (the economy has no `shop`).
 */
export function ShopPanel({
  state,
  onRetry,
  onRoll,
  onBuy,
  now: fixedNow,
  force,
  className,
}: {
  state: PanelState;
  onRetry: () => void;
  onRoll: () => Promise<UseResult>;
  onBuy: (lot: number) => Promise<UseResult>;
  /** Styleguide and visual tests: a still clock */
  now?: Date;
  /** Styleguide only: a state of the lots' buttons */
  force?: string;
  className?: string | undefined;
}) {
  const shop = state.kind === 'ready' ? state.economy.shop : null;
  const expiresAt = shop?.expiresAt ? new Date(shop.expiresAt) : null;
  // The lots' time is the server's (S4-4): the device's clock moved by how far the server's was ahead when the view came
  const offset = state.kind === 'ready' && !fixedNow ? (state.clockOffset ?? 0) : 0;
  const deviceNow = useNow(expiresAt !== null, fixedNow);
  const now = offset === 0 ? deviceNow : new Date(deviceNow.getTime() + offset);
  const [busy, setBusy] = useState<'roll' | number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const titleId = useId();

  if (state.kind === 'ready' && !shop) return null;
  const live = expiresAt !== null && now < expiresAt;
  const lots = live ? (shop?.lots ?? []) : [];
  const rolled = (shop?.lots.length ?? 0) > 0;
  // A reason every lot still on sale shares (the inventory full) is said once under the heading (S4D-5)
  const unsold = lots.filter((l) => !l.sold);
  const sharedWhy =
    unsold.length > 1 && unsold.every((l) => l.refusal !== null && l.refusal === unsold[0]?.refusal)
      ? (unsold[0]?.refusal ?? null)
      : null;
  // With no lots on screen a refused roll's cause is the empty state's text, not a «сделай ролл» above a closed roll
  const emptyWhy =
    lots.length === 0 && shop?.refusal
      ? shop.refusal === 'roll.notEnoughCoins'
        ? ru.shop.emptyPoor(shop.price)
        : refusalText(shop.refusal, ru.shop.refusals)
      : null;

  async function run(what: 'roll' | number, action: () => Promise<UseResult>, success: string) {
    setBusy(what);
    setError(null);
    setDone(null);
    const result = await action();
    setBusy(null);
    if (result.ok) setDone(success);
    else setError(result.message);
  }

  return (
    <Panel aria-labelledby={titleId} data-testid="shop" className={cx('min-w-0', className)}>
      <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
        <h2 id={titleId} className="font-display text-lg font-heavy">
          {ru.shop.title}
        </h2>
        {shop && shop.freeRolls > 0 ? (
          <Chip icon={<Ticket size={16} aria-hidden />}>
            <span data-testid="shop-coupons">{ru.shop.coupons(shop.freeRolls)}</span>
          </Chip>
        ) : null}
      </div>
      {state.kind === 'loading' ? (
        <div className="grid gap-3" aria-busy="true">
          <p className="sr-only">{ru.ui.loading}</p>
          <Skeleton className="h-5 w-2/3" />
          <Skeleton className="h-20 w-full" />
          <Skeleton className="h-12 w-full" />
        </div>
      ) : state.kind === 'failed' ? (
        <ErrorState
          title={ru.shop.loadErrorTitle}
          text={ru.inventory.loadError}
          onRetry={onRetry}
        />
      ) : shop ? (
        <>
          {sharedWhy ? (
            <p className="text-sm text-ink-soft" data-testid="shop-why">
              {refusalText(sharedWhy, ru.shop.refusals)}
            </p>
          ) : null}
          {done ? <Notice tone="success">{done}</Notice> : null}
          {expiresAt !== null && live ? (
            <p className="flex items-center gap-2 font-bold" data-testid="shop-timer">
              <Clock size={20} aria-hidden />
              <span className="tabular-nums">{ru.shop.left(timeLeft(expiresAt, now))}</span>
            </p>
          ) : null}
          {lots.length > 0 ? (
            <ul className="grid gap-3" data-testid="shop-lots">
              {lots.map((lot) => (
                <LotRow
                  key={lot.lot}
                  lot={lot}
                  busy={busy === lot.lot}
                  disabled={busy !== null}
                  why={sharedWhy === null}
                  {...(force ? { force } : {})}
                  onBuy={() => void run(lot.lot, () => onBuy(lot.lot), ru.shop.bought(lot.name))}
                />
              ))}
            </ul>
          ) : (
            <div data-testid={emptyWhy ? 'shop-refusal' : undefined}>
              {rolled ? (
                <EmptyState
                  icon={<Clock size={28} aria-hidden />}
                  title={ru.shop.goneTitle}
                  text={emptyWhy ?? ru.shop.goneText}
                />
              ) : (
                <EmptyState
                  icon={<Store size={28} aria-hidden />}
                  title={ru.shop.emptyTitle}
                  text={emptyWhy ?? ru.shop.emptyText}
                />
              )}
            </div>
          )}
          {error ? <Notice tone="danger">{error}</Notice> : null}
          <div className="grid gap-1">
            <Button
              data-testid="shop-roll"
              icon={shop.payment === 'freeRoll' ? <Ticket size={20} aria-hidden /> : undefined}
              disabled={shop.refusal !== null || busy !== null}
              loading={busy === 'roll'}
              onClick={() => void run('roll', onRoll, ru.shop.rollAnnounce)}
            >
              {shop.payment === 'freeRoll'
                ? ru.shop.rollFree
                : lots.length > 0
                  ? ru.shop.rollAgain(shop.price)
                  : ru.shop.roll(shop.price)}
            </Button>
            <p
              className="text-sm text-ink-soft"
              data-testid={shop.refusal && !emptyWhy ? 'shop-refusal' : undefined}
            >
              {shop.refusal && !emptyWhy
                ? refusalText(shop.refusal, ru.shop.refusals)
                : ru.shop.lead}
            </p>
          </div>
        </>
      ) : null}
    </Panel>
  );
}

/** One lot: what it is, its price, «Купить за N» while the engine sells it, otherwise why not */
export function LotRow({
  lot,
  busy,
  disabled,
  onBuy,
  why = true,
  force,
}: {
  lot: ShopLot;
  busy: boolean;
  disabled: boolean;
  onBuy: () => void;
  /** Whether the lot says its own reason; false — the panel says one reason for all lots (S4D-5) */
  why?: boolean;
  force?: string;
}) {
  const titleId = useId();
  return (
    <li
      aria-labelledby={titleId}
      data-testid={`lot-${lot.lot}`}
      className="grid gap-2 border-t-2 border-muted pt-3 first:border-t-0 first:pt-0"
    >
      <div className="flex flex-wrap items-center gap-2">
        <h3 id={titleId} className="font-display font-heavy wrap-anywhere">
          {lot.name}
        </h3>
        {lot.rarity !== 'common' ? (
          <Badge tone={lot.rarity === 'legendary' ? 'gold' : 'muted'}>
            {ru.inventory.rarity[lot.rarity]}
          </Badge>
        ) : null}
      </div>
      <p className="max-w-prose text-sm wrap-anywhere">{lot.description}</p>
      <div className="flex flex-wrap items-center gap-3">
        <span className="font-bold tabular-nums" data-testid={`lot-price-${lot.lot}`}>
          {ru.shop.price(lot.price)}
        </span>
        {lot.sold ? (
          <Chip tone="success" icon={<Check size={16} aria-hidden />}>
            {ru.shop.sold}
          </Chip>
        ) : lot.refusal === null ? (
          <Button
            data-testid={`buy-${lot.lot}`}
            aria-label={ru.shop.buyNamed(lot.name, lot.price)}
            disabled={disabled}
            loading={busy}
            {...(force ? { force } : {})}
            onClick={onBuy}
          >
            {ru.shop.buyFor(lot.price)}
          </Button>
        ) : !why ? null : (
          <span className="text-sm text-ink-soft" data-testid={`lot-why-${lot.lot}`}>
            {refusalText(lot.refusal, ru.shop.refusals)}
          </span>
        )}
      </div>
    </li>
  );
}
