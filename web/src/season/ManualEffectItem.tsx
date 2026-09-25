import { useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

type Effect = Schemas['ManualEffectView'];

// The engine's limit for comments (Limits.MaxCommentLength, D-102).
const maxCommentLength = 500;
export type EffectOutcome = NonNullable<Schemas['ManualEffectOutcome']>;

/** A manual effect with «Применено» and «Не применимо»; «не применимо» needs a comment (D-102). */
export function ManualEffectItem({
  effect,
  pending,
  resolvable,
  onResolve,
}: {
  effect: Effect;
  pending: boolean;
  /** False once the season is finished: the effect is shown, nothing can be resolved (D-102). */
  resolvable: boolean;
  onResolve: (outcome: EffectOutcome, comment: string | null) => void;
}) {
  const [comment, setComment] = useState('');
  const trimmed = comment.trim();
  const commentId = `effect-comment-${effect.id}`;
  const hintId = `effect-hint-${effect.id}`;

  if (!resolvable) {
    return (
      <li data-testid={`manual-effect-${effect.id}`}>
        <p>{ru.effects.drawEvent(effect.drawEvent, effect.source)}</p>
      </li>
    );
  }

  return (
    <li data-testid={`manual-effect-${effect.id}`}>
      <p>{ru.effects.drawEvent(effect.drawEvent, effect.source)}</p>
      <label htmlFor={commentId}>{ru.effects.comment}</label>
      <input
        id={commentId}
        data-testid={`manual-effect-comment-${effect.id}`}
        value={comment}
        maxLength={maxCommentLength}
        aria-describedby={hintId}
        onChange={(e) => {
          setComment(e.target.value);
        }}
      />
      <button
        data-testid={`manual-effect-applied-${effect.id}`}
        disabled={pending}
        onClick={() => {
          onResolve('applied', trimmed === '' ? null : trimmed);
        }}
      >
        {ru.effects.applied}
      </button>
      <button
        data-testid={`manual-effect-not-applicable-${effect.id}`}
        disabled={pending || trimmed === ''}
        aria-describedby={hintId}
        onClick={() => {
          onResolve('notApplicable', trimmed);
        }}
      >
        {ru.effects.notApplicable}
      </button>
      <p id={hintId}>{ru.effects.commentNeeded}</p>
    </li>
  );
}
