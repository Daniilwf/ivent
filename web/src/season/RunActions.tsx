import { useRef, useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { techRerollReasons, type TechRerollReason } from './techRerollReasons';
import { Button } from '../ui/Button';
import { ConfirmDanger, FormDialog } from '../ui/Dialogs';
import { Select, TextArea } from '../ui/Field';
import { Notice } from '../ui/States';

/**
 * Drop and tech reroll of the active run (D-94), quiet and apart from completing. A drop is confirmed in a window that
 * lists what it costs under the rules in force (`dropPenalty`); while the server says it is early, the window also
 * hints to wait (RR4: only a hint). A tech reroll opens a window with what it does, one of the reasons and a comment
 * («other» needs one); once the window of time has closed the player is sent to the admin. While the server says a wish
 * reroll is open (D-325, D-261: a game of a genre the season lists, within the tech reroll window), «Реролл по желанию»
 * stands next to it: a window says what it does, a yes sends the tech reroll with the reason «wish».
 */
export function RunActions({
  game,
  dropHintMinutes,
  dropPenalty,
  techRerollOpen,
  techRerollUntil = null,
  wishRerollOpen = false,
  onWishReroll,
  frozen = false,
  pending,
  onDrop,
  onTechReroll,
}: {
  /** When the player's own tech reroll window closes (UTC), if the server says */
  techRerollUntil?: string | null;
  /** Whether a wish reroll is open now (the server says; an older server does not) */
  wishRerollOpen?: boolean;
  onWishReroll?: () => void;
  /** The frozen first: a drop costs nothing (D-99) */
  frozen?: boolean;
  game: string;
  dropHintMinutes: number | null;
  dropPenalty: Schemas['DropPenaltyView'] | null;
  techRerollOpen: boolean;
  pending: boolean;
  onDrop: () => void;
  onTechReroll: (reason: TechRerollReason, comment: string | null) => void;
}) {
  const [dropping, setDropping] = useState(false);
  const [rerolling, setRerolling] = useState(false);
  const [wishing, setWishing] = useState(false);
  const wishOpener = useRef<HTMLButtonElement>(null);
  const [reason, setReason] = useState<TechRerollReason | ''>('');
  const [comment, setComment] = useState('');
  const [commentMissing, setCommentMissing] = useState(false);
  const [reasonMissing, setReasonMissing] = useState(false);
  const opener = useRef<HTMLButtonElement>(null);
  const reasonField = useRef<HTMLSelectElement>(null);
  const commentField = useRef<HTMLTextAreaElement>(null);

  function submitTechReroll(event: SyntheticEvent) {
    event.preventDefault();
    const text = comment.trim();
    // Every mistake at once, the focus on the first (no silently disabled button)
    const noReason = reason === '';
    const noComment = reason === 'other' && text === '';
    setReasonMissing(noReason);
    setCommentMissing(noComment);
    if (noReason) {
      reasonField.current?.focus();
      return;
    }
    if (noComment) {
      commentField.current?.focus();
      return;
    }
    setRerolling(false);
    onTechReroll(reason, text === '' ? null : text);
  }

  return (
    <div className="grid gap-1 border-t-2 border-muted pt-3">
      {techRerollOpen ? null : (
        <p data-testid="tech-reroll-closed" className="text-sm text-ink-soft">
          {ru.turn.techRerollClosed}
        </p>
      )}
      <div className="flex flex-wrap items-center justify-between gap-x-5">
        {techRerollOpen ? (
          <Button
            ref={opener}
            variant="link"
            data-testid="tech-reroll"
            disabled={pending}
            onClick={() => {
              setReason('');
              setComment('');
              setCommentMissing(false);
              setReasonMissing(false);
              setRerolling(true);
            }}
          >
            {ru.turn.techReroll}
          </Button>
        ) : null}
        {wishRerollOpen && onWishReroll ? (
          <Button
            ref={wishOpener}
            variant="link"
            data-testid="wish-reroll"
            disabled={pending}
            onClick={() => {
              setWishing(true);
            }}
          >
            {ru.turn.wishReroll}
          </Button>
        ) : null}
        <ConfirmDanger
          testId="drop-confirm"
          open={dropping}
          onOpenChange={setDropping}
          trigger={
            <Button variant="dangerLink" data-testid="drop" disabled={pending} className="ml-auto">
              {ru.turn.drop}
            </Button>
          }
          title={ru.turn.dropTitle(game)}
          consequences={ru.turn.dropConsequences(dropPenalty, frozen)}
          confirm={ru.turn.dropConfirmYes}
          onConfirm={() => {
            setDropping(false);
            onDrop();
          }}
        >
          {dropHintMinutes !== null ? (
            <div data-testid="drop-hint">
              <Notice tone="warning">{ru.turn.dropHint(dropHintMinutes)}</Notice>
            </div>
          ) : null}
        </ConfirmDanger>
      </div>
      <FormDialog
        open={rerolling}
        onOpenChange={setRerolling}
        title={ru.turn.techRerollTitle(game)}
        description={ru.turn.techRerollConsequences}
        returnFocus={opener}
      >
        {techRerollUntil ? (
          <p data-testid="tech-reroll-until" className="text-sm text-ink-soft">
            {ru.turn.techRerollUntil(moscowTime(techRerollUntil))}
          </p>
        ) : null}
        <form
          onSubmit={submitTechReroll}
          noValidate
          data-testid="tech-reroll-form"
          className="grid gap-4"
        >
          <Select
            ref={reasonField}
            error={reasonMissing ? ru.turn.techRerollReasonRequired : undefined}
            data-testid="tech-reroll-reason"
            label={ru.turn.techRerollReason}
            required
            value={reason}
            onChange={(e) => {
              setReason(e.target.value as TechRerollReason | '');
              setReasonMissing(false);
              setCommentMissing(false);
            }}
          >
            <option value="" disabled>
              {ru.turn.techRerollReasonPlaceholder}
            </option>
            {techRerollReasons.map((r) => (
              <option key={r} value={r}>
                {ru.turn.techRerollReasons[r]}
              </option>
            ))}
          </Select>
          <TextArea
            ref={commentField}
            data-testid="tech-reroll-comment"
            label={ru.turn.techRerollComment}
            hint={ru.turn.techRerollCommentHint}
            maxLength={500}
            value={comment}
            error={commentMissing ? ru.turn.techRerollCommentRequired : undefined}
            onChange={(e) => {
              setComment(e.target.value);
              setCommentMissing(false);
            }}
          />
          {/* On a phone the buttons stand full width, the action on top */}
          <div className="grid gap-3 desk:flex desk:flex-row-reverse desk:justify-start">
            <Button type="submit" variant="main" data-testid="tech-reroll-submit" loading={pending}>
              {ru.turn.techRerollSubmit}
            </Button>
            <Button
              data-testid="tech-reroll-cancel"
              onClick={() => {
                setRerolling(false);
              }}
            >
              {ru.turn.techRerollCancel}
            </Button>
          </div>
        </form>
      </FormDialog>
      {onWishReroll ? (
        <FormDialog
          open={wishing}
          onOpenChange={setWishing}
          title={ru.turn.wishRerollTitle(game)}
          description={ru.turn.wishRerollConsequences}
          returnFocus={wishOpener}
          testId="wish-reroll-dialog"
        >
          {techRerollUntil ? (
            <p data-testid="wish-reroll-until" className="text-sm text-ink-soft">
              {ru.turn.wishRerollUntil(moscowTime(techRerollUntil))}
            </p>
          ) : null}
          <div className="grid gap-3 desk:flex desk:flex-row-reverse desk:justify-start">
            <Button
              variant="main"
              data-testid="wish-reroll-confirm"
              loading={pending}
              onClick={() => {
                setWishing(false);
                onWishReroll();
              }}
            >
              {ru.turn.wishRerollConfirm}
            </Button>
            <Button
              data-testid="wish-reroll-cancel"
              onClick={() => {
                setWishing(false);
              }}
            >
              {ru.turn.wishRerollCancel}
            </Button>
          </div>
        </FormDialog>
      ) : null}
    </div>
  );
}
