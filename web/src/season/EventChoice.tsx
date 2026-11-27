import { useId, useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ChoiceGroup } from '../ui/Field';
import { Notice } from '../ui/States';
import { DrawnEventCard } from './DrawnEventCard';

export type EventChoiceProps = {
  choice: Schemas['ChoiceView'];
  busy: boolean;
  disabled?: boolean;
  unavailableReason?: string;
  drawn?: Schemas['DrawnEventView'] | null;
  fresh?: boolean;
  onShown?: (sequence: number) => void;
  onSettled?: ((sequence: number) => void) | undefined;
  onChoose: (optionId: string) => void;
};

/** A server question keeps its draft through live updates; only a different question starts a fresh form. */
export function EventChoice(props: EventChoiceProps) {
  return <AnswerForm key={props.choice.id} {...props} />;
}

function AnswerForm({
  choice,
  busy,
  disabled = false,
  unavailableReason,
  drawn,
  fresh = false,
  onShown,
  onSettled,
  onChoose,
}: EventChoiceProps) {
  const headingId = useId();
  const reasonId = useId();
  const [picked, setPicked] = useState('');
  const selected = choice.options.some((option) => option.id === picked) ? picked : '';
  const blocked = busy || disabled;
  const t = ru.events.choice;
  const blockedReason = unavailableReason ?? (disabled && !busy ? t.unavailable : null);
  const needsChoice = !blockedReason && !blocked && !selected && choice.options.length > 0;
  const form = (
    <div className="grid min-w-0 gap-3">
      {blockedReason ? (
        <p id={reasonId} className="text-sm text-ink-soft">
          {blockedReason}
        </p>
      ) : null}
      <ChoiceGroup
        label={choice.prompt ?? t.prompt}
        options={choice.options.map((option) => ({
          value: option.id,
          label: option.direction ? t.directions[option.direction] : (option.label ?? option.id),
        }))}
        value={selected}
        onChange={setPicked}
        disabled={blocked}
        checkMark
        multiline
      />
      {choice.options.length === 0 ? (
        <Notice tone="warning">{t.empty}</Notice>
      ) : (
        <Button
          variant={disabled ? 'quiet' : 'main'}
          loading={busy}
          disabled={blocked || !selected}
          aria-describedby={blockedReason || needsChoice ? reasonId : undefined}
          onClick={() => {
            if (!blocked && selected) onChoose(selected);
          }}
        >
          {t.answer}
        </Button>
      )}
      {needsChoice ? (
        <p id={reasonId} className="text-sm text-ink-soft">
          {t.chooseAnswer}
        </p>
      ) : null}
    </div>
  );
  return (
    <section
      className="grid min-w-0 gap-3"
      data-testid="event-choice"
      aria-labelledby={headingId}
      aria-busy={busy || undefined}
    >
      {drawn && drawn.objectId === choice.objectId ? (
        <DrawnEventCard
          key={drawn.sequence}
          drawn={drawn}
          fresh={fresh}
          waitingForAnswer
          headingId={headingId}
          level={3}
          onShown={onShown}
          onSettled={onSettled}
        >
          {form}
        </DrawnEventCard>
      ) : (
        <>
          <h3
            id={headingId}
            tabIndex={-1}
            className="font-display text-lg font-heavy wrap-anywhere focus:outline-none"
          >
            {choice.name ?? ru.events.title}
          </h3>
          {choice.description ? <p className="wrap-anywhere">{choice.description}</p> : null}
          {form}
        </>
      )}
    </section>
  );
}
