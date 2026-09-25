import { useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

type Effect = Schemas['ManualEffectView'];
export type EffectOutcome = NonNullable<Schemas['ManualEffectOutcome']>;

/** A manual effect with «Применено» and «Не применимо»; «не применимо» needs a comment (D-102). */
export function ManualEffectItem({
  effect,
  pending,
  onResolve,
}: {
  effect: Effect;
  pending: boolean;
  onResolve: (outcome: EffectOutcome, comment: string | null) => void;
}) {
  const [comment, setComment] = useState('');
  const trimmed = comment.trim();
  const commentId = `effect-comment-${effect.id}`;

  return (
    <li data-testid={`manual-effect-${effect.id}`}>
      <p>{ru.effects.drawEvent(effect.drawEvent, effect.source)}</p>
      <label htmlFor={commentId}>{ru.effects.comment}</label>
      <input
        id={commentId}
        data-testid={`manual-effect-comment-${effect.id}`}
        value={comment}
        maxLength={500}
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
        title={trimmed === '' ? ru.effects.commentNeeded : undefined}
        onClick={() => {
          onResolve('notApplicable', trimmed);
        }}
      >
        {ru.effects.notApplicable}
      </button>
    </li>
  );
}
