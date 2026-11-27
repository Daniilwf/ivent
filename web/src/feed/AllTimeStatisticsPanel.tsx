import type { ReactNode } from 'react';
import { api, type Schemas } from '../api/client';
import { answerOf, useLoaded } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Panel } from '../ui/Surface';

const t = ru.profile;
const recap = ru.seasonRecap;
const count = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 20 });
const percent = new Intl.NumberFormat('ru-RU', { style: 'percent', maximumFractionDigits: 2 });
const refreshEvents = new Set([
  'command-undone',
  'season-player-added',
  'run-completed',
  'completion-rolled',
  'run-dropped',
  'run-hours-corrected',
  'run-difficulty-changed',
  'run-condition-checked',
  'run-dice-modified',
  'run-dice-rerolled',
  'run-reviewed',
  'proof-rejected',
  'coop-reward-split',
  'hostile-received',
]);

const load = async () => answerOf(await api.GET('/api/me/statistics'));

export function AllTimeStatisticsPanel({
  seasonId,
  fallbackCompleted,
  onSignedOut,
}: {
  seasonId: string | null;
  fallbackCompleted: number | null;
  onSignedOut: () => void;
}) {
  const version = useSeasonVersion(seasonId, {
    relevant: (update) => update.types.some((type) => refreshEvents.has(type)),
    throttleMs: 2000,
    refreshOnJoin: true,
  });
  const loaded = useLoaded(load, { onSignedOut, version });

  return (
    <Panel title={t.allTimeTitle} aria-label={t.allTimeTitle}>
      <AsyncState
        loaded={loaded}
        rows={1}
        errorTitle={t.allTimeError}
        notFound={
          <>
            {fallbackCompleted === null ? null : <p>{t.completed(fallbackCompleted)}</p>}
            <p className="text-ink-soft">{t.allTimeUnavailable}</p>
          </>
        }
      >
        {(stats) =>
          stats.seasonCount === 0 ? (
            <p className="text-ink-soft">{t.allTimeEmpty}</p>
          ) : (
            <StatisticsValues stats={stats} />
          )
        }
      </AsyncState>
    </Panel>
  );
}

function StatisticsValues({ stats }: { stats: Schemas['AllTimeStatisticsView'] }) {
  const ratings = [
    ...rating(recap.bestRating, stats.bestRating),
    ...rating(recap.worstRating, stats.worstRating),
  ];
  const rolls = [
    ...roll(recap.bestRoll, stats.bestRoll),
    ...roll(recap.worstRoll, stats.worstRoll),
  ];
  return (
    <>
      <Metrics
        items={[
          { label: t.seasons, value: count.format(stats.seasonCount) },
          { label: recap.completed, value: count.format(stats.completedRuns) },
          { label: recap.hours, value: ru.hours.value(stats.totalHours) },
          { label: t.allTimeDropped, value: count.format(stats.droppedRuns) },
        ]}
      />
      <section className="grid min-w-0 gap-2 border-t border-muted pt-3">
        <h3 className="font-display font-heavy">{recap.ratings}</h3>
        {ratings.length ? (
          <Metrics items={ratings} />
        ) : (
          <p className="text-sm text-ink-soft">{recap.noRatings}</p>
        )}
      </section>
      <section className="grid min-w-0 gap-2 border-t border-muted pt-3">
        <h3 className="font-display font-heavy">{recap.rolls}</h3>
        {rolls.length ? (
          <Metrics items={rolls} />
        ) : (
          <p className="text-sm text-ink-soft">{recap.noRolls}</p>
        )}
      </section>
      <section className="grid min-w-0 gap-2 border-t border-muted pt-3">
        <h3 className="font-display font-heavy">{recap.attacks}</h3>
        <Metrics
          items={[
            { label: recap.attacksMade, value: count.format(stats.attacksMade) },
            { label: recap.attacksReceived, value: count.format(stats.attacksReceived) },
          ]}
        />
      </section>
    </>
  );
}

type Metric = { label: string; value: string; detail?: ReactNode };

function Metrics({ items }: { items: Metric[] }) {
  return (
    <dl className="grid min-w-0 grid-cols-2 gap-3">
      {items.map(({ label, value, detail }) => (
        <div key={label} className="grid min-w-0 content-start gap-1 wrap-anywhere">
          <dt className="text-sm text-ink-soft">{label}</dt>
          <dd className="font-bold tabular-nums">{value}</dd>
          {detail ? <dd className="text-sm text-ink-soft">{detail}</dd> : null}
        </div>
      ))}
    </dl>
  );
}

function rating(label: string, value: number | null): Metric[] {
  return value === null ? [] : [{ label, value: `${count.format(value)} ${recap.ratingUnit}` }];
}

function roll(label: string, value: Schemas['SeasonRollView'] | null): Metric[] {
  if (!value) return [];
  const { label: rangeLabel, range } = recap.rollRange(
    count.format(value.sum),
    count.format(value.minimum),
    count.format(value.maximum),
  );
  return [
    {
      label,
      value: percent.format(value.luck),
      detail: (
        <>
          {rangeLabel} <span className="whitespace-nowrap">{range}</span>
        </>
      ),
    },
  ];
}
