import { useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ConfirmDanger, FormDialog } from '../ui/Dialogs';
import { Select, TextArea } from '../ui/Field';
import { Notice } from '../ui/States';

type Reason = NonNullable<Schemas['TechRerollReason']>;

const reasons: readonly Reason[] = [
  'weakPc',
  'paidUnavailable',
  'doesNotLaunch',
  'emulatorTooSlow',
  'other',
];

/**
 * Drop and tech reroll of the active run (D-94), quiet and apart from completing. A drop is confirmed in a window that
 * lists what it costs under the rules in force (`dropPenalty`); while the server says it is early, the window also
 * hints to wait (RR4: only a hint). A tech reroll opens a window with what it does, one of the reasons and a comment
 * («other» needs one); once the window of time has closed the player is sent to the admin.
 */
export function RunActions({
  game,
  dropHintMinutes,
  dropPenalty,
  techRerollOpen,
  pending,
  onDrop,
  onTechReroll,
}: {
  game: string;
  dropHintMinutes: number | null;
  dropPenalty: Schemas['DropPenaltyView'] | null;
  techRerollOpen: boolean;
  pending: boolean;
  onDrop: () => void;
  onTechReroll: (reason: Reason, comment: string | null) => void;
}) {
  const [dropping, setDropping] = useState(false);
  const [rerolling, setRerolling] = useState(false);
  const [reason, setReason] = useState<Reason | ''>('');
  const [comment, setComment] = useState('');
  const [commentMissing, setCommentMissing] = useState(false);

  function submitTechReroll(event: SyntheticEvent) {
    event.preventDefault();
    if (reason === '') return;
    const text = comment.trim();
    if (reason === 'other' && text === '') {
      setCommentMissing(true);
      return;
    }
    setRerolling(false);
    onTechReroll(reason, text === '' ? null : text);
  }

  return (
    <div className="grid gap-1 border-t-2 border-muted pt-3">
      <div className="flex flex-wrap items-center justify-between gap-x-5">
        {techRerollOpen ? (
          <Button
            variant="link"
            data-testid="tech-reroll"
            disabled={pending}
            onClick={() => {
              setReason('');
              setComment('');
              setCommentMissing(false);
              setRerolling(true);
            }}
          >
            {ru.turn.techReroll}
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
          consequences={ru.turn.dropConsequences(dropPenalty)}
          note={
            dropHintMinutes !== null ? (
              <div data-testid="drop-hint">
                <Notice tone="warning">{ru.turn.dropHint(dropHintMinutes)}</Notice>
              </div>
            ) : null
          }
          confirm={ru.turn.dropConfirmYes}
          onConfirm={() => {
            setDropping(false);
            onDrop();
          }}
        />
      </div>
      {techRerollOpen ? null : (
        <p data-testid="tech-reroll-closed" className="text-sm text-ink-soft">
          {ru.turn.techRerollClosed}
        </p>
      )}
      <FormDialog
        open={rerolling}
        onOpenChange={setRerolling}
        title={ru.turn.techRerollTitle(game)}
        description={ru.turn.techRerollConsequences}
      >
        <form
          onSubmit={submitTechReroll}
          noValidate
          data-testid="tech-reroll-form"
          className="grid gap-4"
        >
          <Select
            data-testid="tech-reroll-reason"
            label={ru.turn.techRerollReason}
            required
            value={reason}
            onChange={(e) => {
              setReason(e.target.value as Reason | '');
              setCommentMissing(false);
            }}
          >
            <option value="" disabled>
              {ru.turn.techRerollReasonPlaceholder}
            </option>
            {reasons.map((r) => (
              <option key={r} value={r}>
                {ru.turn.techRerollReasons[r]}
              </option>
            ))}
          </Select>
          <TextArea
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
          <div className="flex flex-wrap justify-end gap-3">
            <Button
              data-testid="tech-reroll-cancel"
              onClick={() => {
                setRerolling(false);
              }}
            >
              {ru.turn.techRerollCancel}
            </Button>
            <Button
              type="submit"
              variant="main"
              data-testid="tech-reroll-submit"
              disabled={pending || reason === ''}
            >
              {ru.turn.techRerollSubmit}
            </Button>
          </div>
        </form>
      </FormDialog>
    </div>
  );
}
