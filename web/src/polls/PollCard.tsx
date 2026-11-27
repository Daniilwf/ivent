import { Trophy } from 'lucide-react';
import { useId, useRef, useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Summary } from '../ui/Disclosure';
import { ChoiceGroup } from '../ui/Field';
import { Chip } from '../ui/Marks';
import { Notice } from '../ui/States';
import { NominationCandidate } from './NominationCandidate';
import { isOpenPoll } from './pollView';

export type PollCardProps = {
  poll: Schemas['PollView'];
  pending: boolean;
  onVote: (optionId: string) => Promise<void>;
  level?: 2 | 3;
};

export function PollCard(props: PollCardProps) {
  return <PollForm key={props.poll.id} {...props} />;
}

/** The unsent answer belongs to this poll; a server refresh only updates an untouched form. */
function PollForm({ poll, pending, onVote, level = 2 }: PollCardProps) {
  const t = ru.polls;
  const titleId = useId();
  const submitReasonId = useId();
  const title = useRef<HTMLHeadingElement>(null);
  const Heading = level === 3 ? 'h3' : 'h2';
  const open = isOpenPoll(poll);
  const missingCandidate = Boolean(
    poll.nomination && poll.options.some((option) => option.unavailable),
  );
  const endedWithSeason = poll.status === 'open' && !open;
  const [draft, setDraft] = useState({ value: '', dirty: false });
  const [sending, setSending] = useState(false);
  const [failed, setFailed] = useState(false);
  const [accepted, setAccepted] = useState<string | null>(null);
  const [lastView, setLastView] = useState({ vote: poll.myVote, status: poll.status });
  if (lastView.vote !== poll.myVote || lastView.status !== poll.status) {
    setLastView({ vote: poll.myVote, status: poll.status });
    if (poll.status !== 'open' || poll.myVote !== accepted) setAccepted(null);
  }
  const inFlight = useRef(false);
  const ownOption = poll.options.find((option) => option.id === poll.myVote);
  if (draft.value !== '' && !poll.options.some((option) => option.id === draft.value))
    setDraft({ value: '', dirty: true });
  const selected = draft.dirty ? draft.value : (ownOption?.id ?? '');
  const hasVoted = poll.myVote !== null;
  const mayVote =
    open &&
    !missingCandidate &&
    poll.voting?.canVote === true &&
    (!hasVoted || poll.voting.allowChangeVote);
  const busy = pending || sending;
  const validSelection = selected !== '' && poll.options.some((option) => option.id === selected);
  const canSubmit = mayVote && !busy && validSelection && selected !== poll.myVote;
  const action = hasVoted ? t.changeVote : t.vote;
  const submitReason =
    mayVote && !busy && !canSubmit ? (hasVoted ? t.chooseToChangeVote : t.chooseToVote) : null;
  const visibleCounts = poll.status === 'cancelled' ? null : (poll.counts ?? null);
  const counts = new Map(visibleCounts?.map((count) => [count.optionId, count.votes]) ?? []);
  const winner =
    poll.status === 'closed' ? poll.options.find((option) => option.id === poll.result) : null;
  const awaitingResult = open && poll.voting?.reason === 'poll.closed';
  const reason = missingCandidate
    ? t.nominationCandidateMissing
    : !poll.voting
      ? t.eligibilityUnknown
      : awaitingResult
        ? t.votingEnded
        : poll.voting.reason
          ? (ru.rejection[poll.voting.reason] ?? t.unavailable)
          : hasVoted && !poll.voting.allowChangeVote
            ? ru.rejection['poll.alreadyVoted']
            : t.unavailable;

  async function submit(event: SyntheticEvent) {
    event.preventDefault();
    if (!canSubmit || inFlight.current) return;
    inFlight.current = true;
    // The submit button is about to become disabled. Keep a keyboard user in this poll.
    if (event.currentTarget.contains(document.activeElement))
      title.current?.focus({ preventScroll: true });
    setSending(true);
    setFailed(false);
    setAccepted(null);
    try {
      await onVote(selected);
      setDraft({ value: '', dirty: false });
      setAccepted(selected);
    } catch {
      setFailed(true);
    } finally {
      inFlight.current = false;
      setSending(false);
    }
  }

  return (
    <article
      aria-labelledby={titleId}
      data-poll-id={poll.id}
      data-testid={`poll-${poll.id}`}
      className="grid min-w-0 gap-4 rounded-lg bg-card p-4"
      aria-busy={busy || undefined}
    >
      <header className="grid min-w-0 gap-2">
        <Heading
          id={titleId}
          ref={title}
          tabIndex={-1}
          className="max-w-prose font-display text-lg font-heavy wrap-anywhere is-focus:focus-ring"
        >
          {poll.nomination ? ru.nomination.votedTitles[poll.nomination] : poll.question}
        </Heading>
        <div className="flex flex-wrap items-center gap-2 text-sm text-ink-soft">
          {open ? (
            <span>
              {t.until} <time dateTime={poll.closesAt}>{moscowTime(poll.closesAt)}</time>
            </span>
          ) : endedWithSeason ? (
            <span>{t.endedWithSeason}</span>
          ) : (
            <Chip tone={poll.status === 'cancelled' ? 'warning' : 'muted'}>
              {poll.status === 'cancelled' ? t.cancelled : t.closed}
            </Chip>
          )}
          {poll.anonymous ? <Chip>{t.anonymous}</Chip> : null}
        </div>
      </header>
      {poll.nomination ? (
        <NominationMaterials options={poll.options} nomination={poll.nomination} />
      ) : null}
      {ownOption ? (
        <p data-private={poll.anonymous || undefined} className="text-sm font-medium wrap-anywhere">
          {t.ownVote(ownOption.unavailable ? t.nominationCandidateHidden : ownOption.label)}
        </p>
      ) : null}
      {open ? (
        <form
          data-private={poll.anonymous || undefined}
          noValidate
          onSubmit={(event) => {
            void submit(event);
          }}
          className="grid min-w-0 gap-3"
        >
          <ChoiceGroup
            label={mayVote && !hasVoted ? t.legend : t.options}
            options={poll.options.map((option) => {
              const count = counts.get(option.id);
              return {
                value: option.id,
                label: option.unavailable ? t.nominationCandidateHidden : option.label,
                ...(count !== undefined ? { description: t.votes(count) } : {}),
              };
            })}
            value={selected}
            onChange={(value) => {
              if (!mayVote || busy) return;
              setDraft({ value, dirty: value !== poll.myVote });
            }}
            disabled={!mayVote || busy}
            readOnly={!mayVote}
            checkMark
            multiline
          />
          {visibleCounts === null ? (
            <p className="text-sm text-ink-soft">
              {awaitingResult ? t.pendingCounts : t.hiddenCounts}
            </p>
          ) : null}
          {!mayVote ? <p className="text-sm text-ink-soft">{reason}</p> : null}
          {failed && mayVote ? <Notice tone="danger">{t.voteFailed}</Notice> : null}
          {accepted !== null && accepted === poll.myVote ? (
            <Notice tone="success">{t.accepted}</Notice>
          ) : null}
          {mayVote && poll.options.length > 0 ? (
            <>
              <Button
                type="submit"
                variant="main"
                aria-label={action}
                aria-describedby={submitReason ? submitReasonId : undefined}
                loading={busy}
                disabled={!canSubmit}
                className="justify-self-start"
              >
                {action}
              </Button>
              {submitReason ? (
                <p id={submitReasonId} className="text-sm text-ink-soft">
                  {submitReason}
                </p>
              ) : null}
            </>
          ) : null}
        </form>
      ) : (
        <PollResults poll={poll} counts={counts} winner={winner} />
      )}
    </article>
  );
}

function PollResults({
  poll,
  counts,
  winner,
}: {
  poll: Schemas['PollView'];
  counts: ReadonlyMap<string, number>;
  winner: Schemas['PollOptionView'] | null | undefined;
}) {
  const t = ru.polls;
  const total = poll.options.reduce((sum, option) => sum + (counts.get(option.id) ?? 0), 0);

  return (
    <div className="grid min-w-0 gap-3">
      <ul role="list" aria-label={t.options} className="grid gap-3">
        {poll.options.map((option) => {
          const count = counts.get(option.id);
          const share = count !== undefined && total > 0 ? count / total : 0;
          return (
            <li key={option.id} className="grid min-w-0 gap-1">
              <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
                <span className="min-w-0 font-medium wrap-anywhere">
                  {option.unavailable ? t.nominationCandidateHidden : option.label}
                </span>
                {count !== undefined ? (
                  <span className="text-sm text-ink-soft tabular-nums">
                    {t.votes(count)}, {t.voteShare(share)}
                  </span>
                ) : null}
              </div>
              {count !== undefined ? (
                <div aria-hidden className="h-2 overflow-hidden rounded-full bg-muted">
                  <div
                    className={option.id === winner?.id ? 'h-full bg-action' : 'h-full bg-ink-soft'}
                    style={{ width: `${share * 100}%` }}
                  />
                </div>
              ) : null}
            </li>
          );
        })}
      </ul>
      {poll.status === 'closed' ? (
        <p className="flex items-start gap-2 font-medium wrap-anywhere">
          {winner && !winner.unavailable ? (
            <Trophy size={20} aria-hidden className="shrink-0" />
          ) : null}
          <span>
            {winner
              ? winner.unavailable
                ? ru.nominationResults.hiddenWinner
                : t.result(winner.label)
              : poll.result === null
                ? t.noResult
                : t.resultUnavailable}
          </span>
        </p>
      ) : null}
    </div>
  );
}

function NominationMaterials({
  options,
  nomination,
}: {
  options: Schemas['PollOptionView'][];
  nomination: Schemas['VotedNomination'];
}) {
  const materials = options.filter(
    (option) => !option.unavailable && (option.detail || option.href || option.thumbnailUrl),
  );
  if (!materials.length) return null;

  return (
    <details className="min-w-0 rounded-md border-2 border-muted px-3">
      <Summary className="text-sm font-bold">{ru.polls.nominationMaterials}</Summary>
      <ul className="grid divide-y-2 divide-muted">
        {materials.map((option) => (
          <li key={option.id} className="min-w-0 py-3">
            <NominationCandidate candidate={option} nomination={nomination} />
          </li>
        ))}
      </ul>
    </details>
  );
}
