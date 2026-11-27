import { useRef, useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { TextArea } from '../ui/Field';

type Effect = Schemas['ManualEffectView'];

// The engine's limit for comments (Limits.MaxCommentLength, D-102).
const maxCommentLength = 500;
export type EffectOutcome = NonNullable<Schemas['ManualEffectOutcome']>;

/** A manual effect can be played directly. Declining it needs a reason (D-102). */
export function ManualEffectItem({
  effect,
  pending,
  resolvable,
  primary = true,
  onResolve,
}: {
  effect: Effect;
  pending: boolean;
  /** False once the season is finished: the effect is shown, nothing can be resolved (D-102). An IRL effect never is. */
  resolvable: boolean;
  /** Only the task that occupies the main action slot gets the primary button. */
  primary?: boolean;
  onResolve: (outcome: EffectOutcome, comment: string | null) => void;
}) {
  const [declining, setDeclining] = useState(false);
  const [comment, setComment] = useState('');
  const opener = useRef<HTMLButtonElement>(null);
  const trimmed = comment.trim();
  const t = ru.effects;
  const task = t.drawEvent(effect.drawEvent, effect.source);

  function submitDecline(event: SyntheticEvent) {
    event.preventDefault();
    if (pending || trimmed === '') return;
    setDeclining(false);
    onResolve('notApplicable', trimmed);
  }

  // Only the admin resolves an IRL effect, after its proof or «не применимо» (D-509, D-820): the player is shown the text
  if (!resolvable || effect.irl) {
    return (
      <li data-testid={`manual-effect-${effect.id}`}>
        <p>{task}</p>
      </li>
    );
  }

  return (
    <li data-testid={`manual-effect-${effect.id}`} className="grid gap-2">
      <p className="font-bold">{task}</p>
      <p className="text-sm text-ink-soft">{t.taskHint}</p>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <Button
          variant={primary ? 'main' : 'quiet'}
          data-testid={`manual-effect-applied-${effect.id}`}
          disabled={pending}
          onClick={() => {
            onResolve('applied', null);
          }}
        >
          {t.played}
        </Button>
        <Button
          ref={opener}
          variant="link"
          aria-haspopup="dialog"
          disabled={pending}
          onClick={() => {
            setComment('');
            setDeclining(true);
          }}
        >
          {t.unsuitable}
        </Button>
      </div>
      <FormDialog
        open={declining}
        onOpenChange={setDeclining}
        title={t.unsuitableTitle}
        returnFocus={opener}
      >
        <form className="grid gap-4" onSubmit={submitDecline}>
          <TextArea
            label={t.comment}
            hint={t.commentNeeded}
            required
            data-testid={`manual-effect-comment-${effect.id}`}
            value={comment}
            maxLength={maxCommentLength}
            onChange={(event) => {
              setComment(event.target.value);
            }}
          />
          <Button
            type="submit"
            variant="main"
            data-testid={`manual-effect-not-applicable-${effect.id}`}
            disabled={pending || trimmed === ''}
          >
            {t.notApplicable}
          </Button>
        </form>
      </FormDialog>
    </li>
  );
}
