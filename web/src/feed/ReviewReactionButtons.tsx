import { Check, Heart, LoaderCircle } from 'lucide-react';
import { useId, useRef, useState, type KeyboardEvent } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { base } from '../ui/buttonStyles';
import { cx } from '../ui/cx';
import { Notice } from '../ui/States';

type Reaction = Schemas['ReactionView'];
type ReactionIssue = 'refused' | 'tooOften';
export type ChangeReaction = (
  targetId: string,
  emoji: string,
  selected: boolean,
) => Promise<ReactionIssue | null>;
export type ChangeReviewReaction = ChangeReaction;

const t = ru.gamePage.reviewReactions;

export function ReviewReactionButtons({
  runId,
  reactions,
  changeReaction,
  stale,
}: {
  runId: string;
  reactions: readonly Reaction[];
  changeReaction: ChangeReviewReaction;
  stale: boolean;
}) {
  return (
    <ReactionButtons
      targetId={runId}
      groupLabel={t.group}
      reactions={reactions}
      changeReaction={changeReaction}
      stale={stale}
    />
  );
}

export function ReactionButtons({
  targetId,
  groupLabel,
  reactions,
  changeReaction,
  stale,
  compact = false,
}: {
  targetId: string;
  groupLabel: string;
  reactions: readonly Reaction[];
  changeReaction: ChangeReaction;
  stale: boolean;
  compact?: boolean;
}) {
  const descriptionId = useId();
  const writing = useRef(false);
  const chooser = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [pending, setPending] = useState<string | null>(null);
  const [issue, setIssue] = useState<ReactionIssue | null>(null);

  async function toggle({ emoji, selected }: Reaction) {
    if (writing.current) return;
    writing.current = true;
    setPending(emoji);
    setIssue(null);
    try {
      setIssue(await changeReaction(targetId, emoji, !selected));
    } finally {
      writing.current = false;
      setPending(null);
    }
  }

  function renderButton(reaction: Reaction, index: number) {
    const busy = pending === reaction.emoji;
    const countId = `${descriptionId}-${index}`;
    return (
      <button
        key={reaction.emoji}
        type="button"
        aria-label={t.reaction(reaction.emoji)}
        aria-describedby={countId}
        aria-pressed={reaction.selected}
        aria-busy={busy || undefined}
        aria-disabled={pending !== null || undefined}
        className={cx(
          base,
          'min-h-11 min-w-11 gap-1 rounded-full border-2 px-2 text-sm text-ink tabular-nums aria-disabled:cursor-wait aria-disabled:opacity-70 is-active:translate-y-px',
          reaction.selected
            ? 'border-action bg-info-soft is-hover:bg-info-soft'
            : 'border-ink bg-card is-hover:bg-page',
        )}
        onClick={() => void toggle(reaction)}
      >
        <span aria-hidden className="max-w-20 min-w-5 truncate text-base">
          {reaction.emoji}
        </span>
        <span aria-hidden className="min-w-4 text-center">
          {reaction.count}
        </span>
        <span id={countId} className="sr-only">
          {t.count(reaction.count)}
        </span>
        <span aria-hidden className="grid size-3 shrink-0 place-items-center">
          {busy ? (
            <LoaderCircle size={12} className="animate-spin motion-reduce:animate-none" />
          ) : (
            <Check size={12} className={cx(!reaction.selected && 'invisible')} />
          )}
        </span>
      </button>
    );
  }

  const status = issue ? (
    <Notice tone="danger">{t[issue]}</Notice>
  ) : stale ? (
    <Notice tone="warning">{t.stale}</Notice>
  ) : null;

  if (compact) {
    const total = reactions.reduce((sum, reaction) => sum + reaction.count, 0);
    const selected = reactions.some((reaction) => reaction.selected);
    const totalId = `${descriptionId}-total`;
    const panelId = `${descriptionId}-panel`;
    return (
      <div
        role="group"
        aria-label={groupLabel}
        className="contents"
        onKeyDown={(event: KeyboardEvent) => {
          if (!open || event.key !== 'Escape') return;
          event.stopPropagation();
          setOpen(false);
          chooser.current?.focus({ preventScroll: true });
        }}
      >
        <button
          ref={chooser}
          type="button"
          aria-label={t.add}
          aria-describedby={totalId}
          aria-expanded={open}
          aria-controls={panelId}
          className={cx(
            base,
            'row-start-1 min-h-11 min-w-11 gap-1 rounded-md px-1 text-sm tabular-nums is-hover:bg-page',
            selected ? 'text-action' : 'text-ink-soft',
          )}
          onClick={() => {
            setOpen(!open);
          }}
        >
          <Heart size={16} aria-hidden className={cx(selected && 'fill-current')} />
          <span aria-hidden>{total}</span>
          <span id={totalId} className="sr-only">
            {t.count(total)}
          </span>
        </button>
        <div id={panelId} hidden={!open} className="col-span-full pb-2">
          {open ? <div className="flex flex-wrap gap-1">{reactions.map(renderButton)}</div> : null}
        </div>
        {status ? <div className="col-span-full pb-2">{status}</div> : null}
      </div>
    );
  }

  return (
    <div className="grid min-w-0 gap-2">
      <div role="group" aria-label={groupLabel} className="flex flex-wrap gap-1">
        {reactions.map(renderButton)}
      </div>
      {status}
    </div>
  );
}
