import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

const t = ru.seasonRecap;
const count = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 20 });

export type RecapMetric = { label: string; value: string; unit?: string; detail?: string };
export type RecapCard = {
  season: string;
  player: string;
  standing: { place: string; points: string } | null;
  playerToken: number | null;
  completed: RecapMetric;
  hours: RecapMetric;
  ratings: RecapMetric[];
  attacks: RecapMetric[];
  rolls: RecapMetric[] | null;
};

export function recapCard(stats: Schemas['SeasonStatisticsView'], player: string): RecapCard {
  const hasHours = Number.isFinite(stats.totalHours);
  return {
    season: stats.seasonName,
    player,
    standing:
      stats.place == null || stats.points == null
        ? null
        : {
            place: t.place(stats.place, stats.provisional),
            points: t.points(stats.points),
          },
    playerToken: stats.playerToken ?? null,
    completed: { label: t.completed, value: count.format(stats.completedRuns) },
    // A server rolled back to the previous release omits totalHours.
    hours: hasHours
      ? {
          label: t.hours,
          value: count.format(stats.totalHours),
          unit: t.hoursUnit(stats.totalHours),
        }
      : { label: t.hours, value: '—' },
    ratings: [
      ...rating(t.bestRating, stats.bestRating),
      ...rating(t.worstRating, stats.worstRating),
    ],
    attacks: [
      { label: t.attacksMade, value: count.format(stats.attacksMade) },
      { label: t.attacksReceived, value: count.format(stats.attacksReceived) },
    ],
    // A rolled-back server may omit both fields. Do not imply that it measured zero rolls.
    rolls:
      'bestRoll' in stats || 'worstRoll' in stats
        ? [...roll(t.bestRoll, stats.bestRoll), ...roll(t.worstRoll, stats.worstRoll)]
        : null,
  };
}

function rating(label: string, value: number | null): RecapMetric[] {
  return value === null ? [] : [{ label, value: count.format(value), unit: t.ratingUnit }];
}

function roll(label: string, value: Schemas['SeasonRollView'] | null | undefined): RecapMetric[] {
  if (!value) return [];
  const sum = count.format(value.sum);
  return [
    {
      label,
      value: sum,
      detail: t.rollOutcome(sum, count.format(value.maximum)),
    },
  ];
}
