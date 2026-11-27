import type { Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Chip } from '../ui/Marks';

export type GlobalEventBannerProps = {
  events?: readonly Schemas['GlobalEventView'][] | null;
};

export function GlobalEventBanner(props: GlobalEventBannerProps) {
  if (!props.events?.length) return null;
  const t = ru.globalEvents;
  return (
    <div className="grid min-w-0 gap-3">
      {props.events.map((event) => {
        const goal = event.goal;
        const progressText = goal
          ? `${t.progress(goal.progress, goal.target)} ${t.metrics[goal.metric](goal.target)}`
          : '';
        const share =
          goal && goal.target > 0 ? Math.min(Math.max(goal.progress / goal.target, 0), 1) : 0;
        return (
          <section
            key={event.id}
            aria-labelledby={`global-event-${event.id}`}
            className="grid min-w-0 gap-3 rounded-lg bg-info-soft p-4"
          >
            <div className="flex flex-wrap items-start justify-between gap-2">
              <h2
                id={`global-event-${event.id}`}
                className="min-w-0 font-display text-lg font-heavy wrap-anywhere"
              >
                {event.name}
              </h2>
              <p className="text-sm text-ink-soft">
                {t.until} <time dateTime={event.endsAt}>{moscowTime(event.endsAt)}</time>
              </p>
            </div>
            {event.description ? (
              <p className="max-w-prose wrap-anywhere">{event.description}</p>
            ) : null}
            {goal ? (
              <div className="grid gap-2">
                <p className="text-sm font-bold">{t.goalTitle}</p>
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <p className="text-sm font-medium tabular-nums">{progressText}</p>
                  {goal.reached ? <Chip tone="success">{t.reached}</Chip> : null}
                </div>
                <div
                  role="progressbar"
                  aria-label={t.goal(event.name)}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={share * 100}
                  aria-valuetext={progressText}
                  className="h-2 overflow-hidden rounded-full bg-muted"
                >
                  <span
                    className="block h-full rounded-full bg-info"
                    style={{ width: `${share * 100}%` }}
                  />
                </div>
              </div>
            ) : null}
          </section>
        );
      })}
    </div>
  );
}
