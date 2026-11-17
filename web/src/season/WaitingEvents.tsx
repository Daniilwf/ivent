import { useId } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Tag } from '../ui/Marks';
import { RunConditionList } from './RunConditions';

export type WaitingEventsProps = {
  events?: readonly Schemas['WaitingEventView'][] | null;
  conditions?: readonly Schemas['RunConditionView'][] | null;
};

export function WaitingEvents({ events, conditions }: WaitingEventsProps) {
  const eventsTitleId = useId();
  const conditionsTitleId = useId();
  if (!events?.length && !conditions?.length) return null;

  return (
    <div className="grid gap-4">
      {events?.length ? (
        <section aria-labelledby={eventsTitleId} className="grid gap-2">
          <h3 id={eventsTitleId} className="font-display font-heavy">
            {ru.events.waitingTitle}
          </h3>
          <ul className="grid gap-3">
            {events.map((event, index) => (
              <li key={`${event.kind}:${event.objectId}:${String(index)}`} className="grid gap-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-bold wrap-anywhere">{event.name}</span>
                  <Tag>{ru.events.kinds[event.kind]}</Tag>
                </div>
                <p className="max-w-prose text-sm text-ink-soft wrap-anywhere">
                  {event.description}
                </p>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
      {conditions?.length ? (
        <section aria-labelledby={conditionsTitleId} className="grid gap-2">
          <h3 id={conditionsTitleId} className="font-display font-heavy">
            {ru.events.waitingConditionsTitle}
          </h3>
          <RunConditionList conditions={conditions} />
        </section>
      ) : null}
    </div>
  );
}
