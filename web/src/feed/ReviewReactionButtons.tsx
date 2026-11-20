import { Check, LoaderCircle } from 'lucide-react';
import {
  useCallback,
  useId,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
  type Ref,
} from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { base } from '../ui/buttonStyles';
import { cx } from '../ui/cx';
import { SummaryButton } from '../ui/Disclosure';
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
  const group = useRef<HTMLDivElement>(null);
  const add = useRef<HTMLButtonElement>(null);
  const lastChoice = useRef<string | null>(null);
  const [pending, setPending] = useState<string | null>(null);
  const [issue, setIssue] = useState<ReactionIssue | null>(null);

  // A focused button can disappear when the last reaction of its kind is removed or the final choice is added.
  const keepFocus = useCallback((button: HTMLButtonElement | null) => {
    if (!button) return;
    return () => {
      const doc = button.ownerDocument;
      if (doc.activeElement !== button) return;
      queueMicrotask(() => {
        if (button.isConnected || doc.activeElement !== doc.body) return;
        const inline = Array.from(
          group.current?.querySelectorAll<HTMLButtonElement>('button[data-reaction-inline]') ?? [],
        );
        const chosen = inline.find(
          (option) => option.dataset.reactionInline === lastChoice.current,
        );
        (add.current ?? chosen ?? inline[0])?.focus({ preventScroll: true });
      });
    };
  }, []);

  const keepAdd = useCallback(
    (button: HTMLButtonElement | null) => {
      add.current = button;
      const cleanUp = keepFocus(button);
      return () => {
        add.current = null;
        cleanUp?.();
      };
    },
    [keepFocus],
  );

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

  const buttons = reactions.map((reaction, index) => ({ reaction, index }));
  const showInline = ({ reaction }: (typeof buttons)[number]) =>
    !compact || reaction.selected || reaction.count > 0;
  const remaining = buttons.filter((button) => !showInline(button));

  function renderButton({ reaction, index }: (typeof buttons)[number], close?: () => void) {
    const busy = pending === reaction.emoji;
    const countId = `${descriptionId}-${index}`;
    return (
      <button
        key={reaction.emoji}
        ref={compact ? keepFocus : undefined}
        type="button"
        aria-label={t.reaction(reaction.emoji)}
        aria-describedby={countId}
        aria-pressed={reaction.selected}
        aria-busy={busy || undefined}
        aria-disabled={pending !== null || undefined}
        data-reaction-inline={compact && !close ? reaction.emoji : undefined}
        className={cx(
          base,
          'min-h-11 min-w-11 gap-1 rounded-full border-2 px-2 text-sm text-ink tabular-nums aria-disabled:cursor-wait aria-disabled:opacity-70 is-active:translate-y-px',
          reaction.selected
            ? 'border-action bg-info-soft is-hover:bg-info-soft'
            : 'border-ink bg-card is-hover:bg-page',
        )}
        onClick={() => {
          if (writing.current) return;
          if (close) {
            lastChoice.current = reaction.emoji;
            close();
          }
          void toggle(reaction);
        }}
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

  return (
    <div className="grid min-w-0 gap-2">
      <div ref={group} role="group" aria-label={groupLabel} className="flex flex-wrap gap-1">
        {buttons.filter(showInline).map((button) => renderButton(button))}
        {remaining.length > 0 ? (
          <ReactionPicker
            trigger={keepAdd}
            focusTrigger={() => add.current?.focus({ preventScroll: true })}
            disabled={pending !== null}
            busy={remaining.some(({ reaction }) => reaction.emoji === pending)}
          >
            {(close) => remaining.map((button) => renderButton(button, close))}
          </ReactionPicker>
        ) : null}
      </div>
      {issue ? <Notice tone="danger">{t[issue]}</Notice> : null}
      {stale && !issue ? <Notice tone="warning">{t.stale}</Notice> : null}
    </div>
  );
}

function ReactionPicker({
  trigger,
  focusTrigger,
  disabled,
  busy,
  children,
}: {
  trigger: Ref<HTMLButtonElement>;
  focusTrigger: () => void;
  disabled: boolean;
  busy: boolean;
  children: (close: () => void) => ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const panel = useId();

  function close() {
    setOpen(false);
    focusTrigger();
  }

  function onKeyDown(event: KeyboardEvent) {
    if (!open || event.key !== 'Escape') return;
    event.stopPropagation();
    close();
  }

  return (
    <div className="contents" onKeyDown={onKeyDown}>
      <SummaryButton
        ref={trigger}
        open={open}
        aria-controls={panel}
        aria-disabled={disabled || undefined}
        busy={busy}
        className="px-2 text-sm font-medium text-ink-soft is-hover:bg-page aria-disabled:cursor-wait aria-disabled:opacity-70"
        onClick={() => {
          if (!disabled) setOpen(!open);
        }}
      >
        {t.add}
      </SummaryButton>
      <div id={panel} hidden={!open} className="basis-full">
        {open ? <div className="flex flex-wrap gap-1">{children(close)}</div> : null}
      </div>
    </div>
  );
}
