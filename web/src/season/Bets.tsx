import { Check, Clock, HandCoins, Hourglass, Undo2, X } from 'lucide-react';
import { useId, useRef, useState, type ReactNode } from 'react';
import type { Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { Summary } from '../ui/Disclosure';
import { ChoiceGroup, Field } from '../ui/Field';
import { Chip } from '../ui/Marks';
import { Skeleton } from '../ui/Progress';
import { EmptyState, ErrorState, Notice } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { Panel } from '../ui/Surface';
import { cx } from '../ui/cx';
import { payoutOf, refusalText, type PanelState, type UseResult } from './economy';

export type BetRun = Schemas['BetRunView'];
export type MyBet = Schemas['BetView'];

/** A bet as the sheet sends it */
export type NewBet = { onPlayerId: string; days: number; stake: number };

/**
 * Bets on the season screen (4.11c, D-414, D-447): the runs of others a bet is accepted on now — whose, which game, until
 * when — each with «Поставить», then my bets with their status in words and an icon. Hidden when the season's bets are
 * off (the economy has no `bets`).
 */
export function BetsPanel({
  state,
  onRetry,
  onBet,
  force,
  className,
}: {
  state: PanelState;
  onRetry: () => void;
  onBet: (bet: NewBet) => Promise<UseResult>;
  /** Styleguide only: a state of the «Поставить» buttons */
  force?: string;
  className?: string | undefined;
}) {
  const bets = state.kind === 'ready' ? state.economy.bets : null;
  const [betting, setBetting] = useState<BetRun | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const opener = useRef<HTMLButtonElement | null>(null);
  const titleId = useId();
  if (state.kind === 'ready' && !bets) return null;

  return (
    <Panel aria-labelledby={titleId} data-testid="bets" className={cx('min-w-0', className)}>
      <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
        <h2 id={titleId} className="font-display text-lg font-heavy">
          {ru.bets.title}
        </h2>
        {bets ? (
          <span className="text-sm text-ink-soft tabular-nums" data-testid="bets-open">
            {ru.bets.open(bets.openBets, bets.maxOpenBets)}
          </span>
        ) : null}
      </div>
      {state.kind === 'loading' ? (
        <div className="grid gap-3" aria-busy="true">
          <p className="sr-only">{ru.ui.loading}</p>
          <Skeleton className="h-5 w-2/3" />
          <Skeleton className="h-16 w-full" />
          <Skeleton className="h-16 w-full" />
        </div>
      ) : state.kind === 'failed' ? (
        <ErrorState
          title={ru.bets.loadErrorTitle}
          text={ru.inventory.loadError}
          onRetry={onRetry}
        />
      ) : bets ? (
        <BetsBody
          bets={bets}
          done={done}
          titleId={titleId}
          {...(force ? { force } : {})}
          onBet={(run, button) => {
            opener.current = button;
            setDone(null);
            setBetting(run);
          }}
        />
      ) : null}
      {betting && bets ? (
        <BetDialog
          run={betting}
          stakeLimit={bets.stakeLimit}
          returnFocus={opener}
          onClose={() => {
            setBetting(null);
          }}
          onBet={async (bet) => {
            const result = await onBet(bet);
            if (result.ok) {
              setBetting(null);
              setDone(ru.bets.placed(betting.name));
            }
            return result;
          }}
        />
      ) : null}
    </Panel>
  );
}

/** The rows shown before the rest fold behind «Ещё N игроков» (S4D-3) */
const shownRuns = 5;

/**
 * The panel's content: my bets first, then the runs open for a bet (S4D-3). Nothing at all to show is one empty state
 * with the lead folded in (S4D-4); a sub-heading stands only over its content.
 */
function BetsBody({
  bets,
  done,
  titleId,
  force,
  onBet,
}: {
  bets: NonNullable<Schemas['EconomyView']['bets']>;
  done: string | null;
  titleId: string;
  force?: string;
  onBet: (run: BetRun, button: HTMLButtonElement) => void;
}) {
  const runs = bets.refusal ? [] : bets.runs;
  if (runs.length === 0 && bets.bets.length === 0) {
    return (
      <div className="grid gap-3" data-testid={bets.refusal ? 'bets-refusal' : 'bets-empty'}>
        {done ? <Notice tone="success">{done}</Notice> : null}
        <EmptyState
          icon={<HandCoins size={28} aria-hidden />}
          title={
            bets.refusal
              ? refusalText(bets.refusal, ru.bets.refusals).replace(/\.$/, '')
              : ru.bets.emptyTitle
          }
          text={bets.refusal ? ru.bets.lead : `${ru.bets.lead} ${ru.bets.emptyText}`}
        />
      </div>
    );
  }
  // One deadline for every run is said once under the heading, not on each row
  const shared =
    runs.length > 1 && new Set(runs.map((r) => r.closesAt)).size === 1
      ? runs[0]?.closesAt
      : undefined;
  const row = (run: BetRun) => (
    <BetRunRow
      key={run.runId}
      run={run}
      deadline={shared === undefined}
      {...(force ? { force } : {})}
      onBet={(button) => {
        onBet(run, button);
      }}
    />
  );
  return (
    <>
      <p className="max-w-prose text-sm text-ink-soft">{ru.bets.lead}</p>
      {done ? <Notice tone="success">{done}</Notice> : null}
      {bets.bets.length > 0 ? (
        <section aria-labelledby={`${titleId}-mine`} className="grid gap-2">
          <h3 id={`${titleId}-mine`} className="font-display font-heavy">
            {ru.bets.mineTitle}
          </h3>
          <ul className="grid gap-2" data-testid="my-bets">
            {bets.bets.map((bet) => (
              <MyBetRow key={bet.betId} bet={bet} />
            ))}
          </ul>
        </section>
      ) : null}
      <section aria-labelledby={`${titleId}-runs`} className="grid gap-2">
        <h3 id={`${titleId}-runs`} className="font-display font-heavy">
          {ru.bets.runsTitle}
        </h3>
        {bets.refusal ? (
          <p className="text-sm text-ink-soft" data-testid="bets-refusal">
            {refusalText(bets.refusal, ru.bets.refusals)}
          </p>
        ) : runs.length === 0 ? (
          <p className="text-sm text-ink-soft">{ru.bets.emptyTitle}</p>
        ) : (
          <>
            {shared ? (
              <p className="flex items-center gap-2 text-sm text-ink-soft tabular-nums">
                <Clock size={16} aria-hidden />
                {ru.bets.until(moscowTime(shared))}
              </p>
            ) : null}
            <ul className="grid gap-2" data-testid="bet-runs">
              {runs.slice(0, shownRuns).map(row)}
            </ul>
            {runs.length > shownRuns ? (
              <details data-testid="bet-runs-more" className="grid gap-2">
                <Summary className="font-bold">{ru.bets.more(runs.length - shownRuns)}</Summary>
                <ul className="grid gap-2">{runs.slice(shownRuns).map(row)}</ul>
              </details>
            ) : null}
          </>
        )}
      </section>
    </>
  );
}

/**
 * A run open for a bet in one compact line (S4D-3): the player, the game, «Поставить»; its hours only when known, its
 * deadline only when the runs do not share one
 */
export function BetRunRow({
  run,
  onBet,
  deadline = true,
  force,
}: {
  run: BetRun;
  onBet: (button: HTMLButtonElement) => void;
  /** Whether the row says when bets on it close (false: the panel says one deadline for all) */
  deadline?: boolean;
  force?: string;
}) {
  const nameId = useId();
  return (
    <li
      aria-labelledby={nameId}
      data-testid={`bet-run-${run.playerId}`}
      className="flex min-w-0 items-center gap-3 border-t-2 border-muted pt-2 first:border-t-0 first:pt-0"
    >
      <Sticker player={{ name: run.name, token: run.token }} size={36} />
      <div className="grid min-w-0 flex-1">
        <h4 id={nameId} className="font-display font-heavy wrap-anywhere">
          {run.name}
        </h4>
        <span className="text-sm wrap-anywhere">{run.gameTitle}</span>
        {run.hours != null || deadline ? (
          <span className="flex flex-wrap gap-x-3 text-sm text-ink-soft tabular-nums">
            {run.hours != null ? (
              <span className="inline-flex items-center gap-1">
                <Hourglass size={16} aria-hidden />
                {ru.bets.hours(run.hours)}
              </span>
            ) : null}
            {deadline ? (
              <span className="inline-flex items-center gap-1">
                <Clock size={16} aria-hidden />
                {ru.bets.until(moscowTime(run.closesAt))}
              </span>
            ) : null}
          </span>
        ) : null}
      </div>
      <Button
        className="shrink-0"
        data-testid={`bet-on-${run.playerId}`}
        aria-label={ru.bets.placeOn(run.name)}
        {...(force ? { force } : {})}
        onClick={(e) => {
          onBet(e.currentTarget);
        }}
      >
        {ru.bets.place}
      </Button>
    </li>
  );
}

const statusIcons: Record<MyBet['status'], ReactNode> = {
  open: <Clock size={16} aria-hidden />,
  won: <Check size={16} aria-hidden />,
  lost: <X size={16} aria-hidden />,
  revoked: <Undo2 size={16} aria-hidden />,
};
const statusTones = { open: 'info', won: 'success', lost: 'muted', revoked: 'warning' } as const;

/** One of my bets: on whom and which game, the stake and its multiplier, the deadline, the status in words and an icon */
export function MyBetRow({ bet }: { bet: MyBet }) {
  return (
    <li
      data-testid={`bet-${bet.betId}`}
      className="grid gap-1 border-t-2 border-muted pt-2 first:border-t-0 first:pt-0"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="font-bold wrap-anywhere">{ru.bets.on(bet.onPlayer, bet.gameTitle)}</span>
        <Chip tone={statusTones[bet.status]} icon={statusIcons[bet.status]}>
          <span data-testid={`bet-status-${bet.betId}`}>
            {ru.bets.status[bet.status]}
            {bet.status === 'won' && bet.payout > 0 ? ` ${ru.bets.wonPayout(bet.payout)}` : ''}
          </span>
        </Chip>
      </div>
      <span className="text-sm text-ink-soft tabular-nums">
        {ru.bets.stakeOf(bet.stake, bet.multiplier)}
        {bet.status === 'open' ? `. ${ru.bets.deadlineAt(moscowTime(bet.deadline))}` : ''}
      </span>
    </li>
  );
}

/**
 * The sheet of a bet: the deadline with its multiplier, the stake (1 to my limit), the win said before the press. A
 * refusal of the server stays in the sheet with the choices made.
 */
export function BetDialog({
  run,
  stakeLimit,
  onBet,
  onClose,
  returnFocus,
}: {
  run: BetRun;
  stakeLimit: number;
  onBet: (bet: NewBet) => Promise<UseResult>;
  onClose: () => void;
  returnFocus?: React.RefObject<HTMLElement | null>;
}) {
  const [days, setDays] = useState(String(run.options[0]?.days ?? 1));
  const [stake, setStake] = useState('1');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const option = run.options.find((o) => String(o.days) === days) ?? run.options[0];
  const amount = /^\d+$/.test(stake) ? Number(stake) : NaN;
  const valid = Number.isInteger(amount) && amount >= 1 && amount <= stakeLimit;

  return (
    <FormDialog
      open
      onOpenChange={(next) => {
        if (!next && !busy) onClose();
      }}
      title={ru.bets.dialogTitle(run.name)}
      testId="bet-sheet"
      {...(returnFocus ? { returnFocus } : {})}
    >
      <p className="max-w-prose wrap-anywhere">{ru.bets.dialogGame(run.gameTitle)}</p>
      <p className="flex items-center gap-2 text-sm text-ink-soft">
        <Hourglass size={16} aria-hidden />
        {run.hours != null ? ru.bets.hours(run.hours) : ru.bets.noHours}
      </p>
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (!valid || !option || busy) return;
          setBusy(true);
          setError(null);
          void onBet({ onPlayerId: run.playerId, days: option.days, stake: amount }).then(
            (result) => {
              setBusy(false);
              if (!result.ok) setError(result.message);
            },
          );
        }}
      >
        <ChoiceGroup
          label={ru.bets.deadline}
          {...(new Set(run.options.map((o) => o.multiplier)).size > 1
            ? { hint: ru.bets.deadlineHint }
            : {})}
          data-testid="bet-days"
          options={run.options.map((o) => ({
            value: String(o.days),
            label: ru.bets.option(o.days, o.multiplier),
          }))}
          value={days}
          onChange={(value) => {
            setDays(value);
            setError(null);
          }}
        />
        <Field
          label={ru.bets.stake}
          hint={ru.bets.stakeHint(stakeLimit)}
          errorRestatesHint
          data-testid="bet-stake"
          type="number"
          inputMode="numeric"
          min={1}
          max={stakeLimit}
          step={1}
          value={stake}
          error={stake !== '' && !valid ? ru.bets.stakeInvalid(stakeLimit) : undefined}
          onChange={(e) => {
            setStake(e.target.value);
            setError(null);
          }}
        />
        {/* What comes back, said before the press; an invalid stake is said at its field only (S4D-7) */}
        {valid && option ? (
          <p className="font-bold tabular-nums" data-testid="bet-payout">
            {ru.bets.payout(payoutOf(amount, option.multiplier), amount)}
          </p>
        ) : null}
        {error ? <Notice tone="danger">{error}</Notice> : null}
        <Button
          type="submit"
          variant="main"
          data-testid="bet-confirm"
          disabled={!valid}
          loading={busy}
        >
          {valid ? ru.bets.confirm(amount) : ru.bets.place}
        </Button>
      </form>
    </FormDialog>
  );
}
