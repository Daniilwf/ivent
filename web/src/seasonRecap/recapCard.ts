import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

const t = ru.seasonRecap;
const count = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 20 });

export type RecapMetric = { label: string; value: string; unit?: string };
export type RecapCard = {
  season: string;
  player: string;
  completed: RecapMetric;
  hours: RecapMetric;
  ratings: RecapMetric[];
  attacks: RecapMetric[];
};

export function recapCard(stats: Schemas['SeasonStatisticsView'], player: string): RecapCard {
  return {
    season: stats.seasonName,
    player,
    completed: { label: t.completed, value: count.format(stats.completedRuns) },
    // A server rolled back to the previous release omits totalHours.
    hours: {
      label: t.hours,
      value: Number.isFinite(stats.totalHours) ? count.format(stats.totalHours) : '—',
    },
    ratings: [
      ...rating(t.bestRating, stats.bestRating),
      ...rating(t.worstRating, stats.worstRating),
    ],
    attacks: [
      { label: t.attacksMade, value: count.format(stats.attacksMade) },
      { label: t.attacksReceived, value: count.format(stats.attacksReceived) },
    ],
  };
}

function rating(label: string, value: number | null): RecapMetric[] {
  return value === null ? [] : [{ label, value: count.format(value), unit: t.ratingUnit }];
}
