import { useId } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Chip, Tag } from '../ui/Marks';

export type RunConditionsProps = {
  conditions?: readonly Schemas['RunConditionView'][] | null;
  check?: Schemas['RunConditionCheckView'] | null;
  resultNote?: string | null;
};

/** The server fixes both bounds and order; repeated objects may carry different conditions. */
export function RunConditionList({
  conditions,
}: {
  conditions: readonly Schemas['RunConditionView'][];
}) {
  return (
    <ul className="grid gap-3">
      {conditions.map((condition, index) => (
        <li
          key={`${condition.kind ?? ''}:${condition.objectId}:${String(index)}`}
          className="grid gap-1"
        >
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-bold wrap-anywhere">{condition.name}</span>
            {condition.kind ? <Tag>{ru.events.kinds[condition.kind]}</Tag> : null}
          </div>
          {condition.difficultyAtLeast || condition.difficultyAtMost ? (
            <p className="flex flex-wrap gap-x-4 gap-y-1 text-sm text-ink-soft">
              {condition.difficultyAtLeast ? (
                <span>{ru.runConditions.atLeast(ru.difficulty[condition.difficultyAtLeast])}</span>
              ) : null}
              {condition.difficultyAtMost ? (
                <span>{ru.runConditions.atMost(ru.difficulty[condition.difficultyAtMost])}</span>
              ) : null}
            </p>
          ) : null}
        </li>
      ))}
    </ul>
  );
}

export function RunConditions({ conditions, check, resultNote }: RunConditionsProps) {
  const titleId = useId();
  if (!conditions?.length) return null;

  return (
    <section aria-labelledby={titleId} className="grid gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <h3 id={titleId} className="font-display font-heavy">
          {ru.runConditions.title}
        </h3>
        {check ? (
          <Chip tone={check.waived ? 'muted' : check.met ? 'success' : 'warning'}>
            {check.waived
              ? ru.runConditions.waived
              : check.met
                ? ru.runConditions.met
                : ru.runConditions.unmet}
          </Chip>
        ) : null}
      </div>
      {resultNote ? <p className="text-sm text-ink-soft">{resultNote}</p> : null}
      <RunConditionList conditions={conditions} />
    </section>
  );
}
