import { useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

type Reason = NonNullable<Schemas['TechRerollReason']>;

const reasons: readonly Reason[] = [
  'weakPc',
  'paidUnavailable',
  'doesNotLaunch',
  'emulatorTooSlow',
  'other',
];

/**
 * Drop and tech reroll of the active run (D-94). A drop is confirmed with its penalty named as the rules say;
 * while the server says it is early, the confirmation also hints to wait (RR4: only a hint). A tech reroll asks for one
 * of the reasons, «other» needs a comment; once the window has closed the player is sent to the admin.
 */
export function RunActions({
  dropHintMinutes,
  dropPenalty,
  techRerollOpen,
  pending,
  onDrop,
  onTechReroll,
}: {
  dropHintMinutes: number | null;
  dropPenalty: Schemas['DropPenaltyView'] | null;
  techRerollOpen: boolean;
  pending: boolean;
  onDrop: () => void;
  onTechReroll: (reason: Reason, comment: string | null) => void;
}) {
  const [mode, setMode] = useState<'none' | 'drop' | 'techReroll'>('none');
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
    setMode('none');
    onTechReroll(reason, text === '' ? null : text);
  }

  if (mode === 'drop') {
    return (
      <div role="group" data-testid="drop-confirm">
        <p>{ru.turn.dropConfirm(dropPenalty)}</p>
        {dropHintMinutes !== null && (
          <p data-testid="drop-hint">{ru.turn.dropHint(dropHintMinutes)}</p>
        )}
        <button
          data-testid="drop-confirm-yes"
          disabled={pending}
          onClick={() => {
            setMode('none');
            onDrop();
          }}
        >
          {ru.turn.dropConfirmYes}
        </button>
        <button
          data-testid="drop-confirm-no"
          onClick={() => {
            setMode('none');
          }}
        >
          {ru.turn.dropConfirmNo}
        </button>
      </div>
    );
  }

  if (mode === 'techReroll') {
    return (
      <form onSubmit={submitTechReroll} data-testid="tech-reroll-form">
        <label>
          {ru.turn.techRerollReason}
          <select
            data-testid="tech-reroll-reason"
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
          </select>
        </label>
        <label>
          {ru.turn.techRerollComment}
          <textarea
            data-testid="tech-reroll-comment"
            maxLength={500}
            value={comment}
            onChange={(e) => {
              setComment(e.target.value);
              setCommentMissing(false);
            }}
          />
        </label>
        {commentMissing && <p role="alert">{ru.turn.techRerollCommentRequired}</p>}
        <button type="submit" data-testid="tech-reroll-submit" disabled={pending || reason === ''}>
          {ru.turn.techRerollSubmit}
        </button>
        <button
          type="button"
          data-testid="tech-reroll-cancel"
          onClick={() => {
            setMode('none');
          }}
        >
          {ru.turn.techRerollCancel}
        </button>
      </form>
    );
  }

  return (
    <div role="group">
      <button
        data-testid="drop"
        disabled={pending}
        onClick={() => {
          setMode('drop');
        }}
      >
        {ru.turn.drop}
      </button>
      {techRerollOpen ? (
        <button
          data-testid="tech-reroll"
          disabled={pending}
          onClick={() => {
            setMode('techReroll');
          }}
        >
          {ru.turn.techReroll}
        </button>
      ) : (
        <p data-testid="tech-reroll-closed">{ru.turn.techRerollClosed}</p>
      )}
    </div>
  );
}
