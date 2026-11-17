import { useId, useRef, useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ChoiceGroup } from '../ui/Field';
import { Chip } from '../ui/Marks';
import { Notice } from '../ui/States';

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
  const title = useRef<HTMLHeadingElement>(null);
  const Heading = level === 3 ? 'h3' : 'h2';
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
    poll.status === 'open' &&
    poll.voting?.canVote === true &&
    (!hasVoted || poll.voting.allowChangeVote);
  const busy = pending || sending;
  const validSelection = selected !== '' && poll.options.some((option) => option.id === selected);
  const canSubmit = mayVote && !busy && validSelection && selected !== poll.myVote;
  const action = hasVoted ? t.changeVote : t.vote;
  const visibleCounts = poll.status === 'cancelled' ? null : poll.counts;
  const counts = new Map(visibleCounts?.map((count) => [count.optionId, count.votes]) ?? []);
  const winner =
    poll.status === 'closed' ? poll.options.find((option) => option.id === poll.result) : null;
  const awaitingResult = poll.status === 'open' && poll.voting?.reason === 'poll.closed';
  const reason = !poll.voting
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
          {poll.question}
        </Heading>
        <div className="flex flex-wrap items-center gap-2 text-sm text-ink-soft">
          {poll.status === 'open' ? (
            <span>
              {t.until} <time dateTime={poll.closesAt}>{moscowTime(poll.closesAt)}</time>
            </span>
          ) : (
            <Chip tone={poll.status === 'cancelled' ? 'warning' : 'muted'}>
              {poll.status === 'cancelled' ? t.cancelled : t.closed}
            </Chip>
          )}
          {poll.anonymous ? <Chip>{t.anonymous}</Chip> : null}
        </div>
      </header>
      {ownOption ? (
        <p data-private={poll.anonymous || undefined} className="text-sm font-medium wrap-anywhere">
          {t.ownVote(ownOption.label)}
        </p>
      ) : null}
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
              label: option.label,
              ...(count !== undefined ? { description: t.votes(count) } : {}),
            };
          })}
          value={poll.status === 'open' ? selected : (ownOption?.id ?? '')}
          onChange={(value) => {
            if (!mayVote || busy) return;
            setDraft({ value, dirty: value !== poll.myVote });
          }}
          disabled={!mayVote || busy}
          readOnly={!mayVote}
          checkMark
          multiline
        />
        {poll.status === 'open' && visibleCounts === null ? (
          <p className="text-sm text-ink-soft">
            {awaitingResult ? t.pendingCounts : t.hiddenCounts}
          </p>
        ) : null}
        {poll.status === 'closed' ? (
          <p className="font-medium wrap-anywhere">
            {winner
              ? t.result(winner.label)
              : poll.result === null
                ? t.noResult
                : t.resultUnavailable}
          </p>
        ) : null}
        {poll.status === 'open' && !mayVote ? (
          <p className="text-sm text-ink-soft">{reason}</p>
        ) : null}
        {failed && mayVote ? <Notice tone="danger">{t.voteFailed}</Notice> : null}
        {accepted !== null && accepted === poll.myVote && poll.status === 'open' ? (
          <Notice tone="success">{t.accepted}</Notice>
        ) : null}
        {mayVote && poll.options.length > 0 ? (
          <Button
            type="submit"
            variant="main"
            aria-label={action}
            loading={busy}
            disabled={!canSubmit}
            className="justify-self-start"
          >
            {action}
          </Button>
        ) : null}
      </form>
    </article>
  );
}
