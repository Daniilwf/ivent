import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

// H7: the rules page is built from the season's ruleset, so every number on it is the one in force (SPEC «Правила на
// сайте»). This module turns the ruleset into the page's sentences and the history's «было/стало» into words.

export type Ruleset = Schemas['Ruleset'];
type Difficulty = 'easy' | 'normal' | 'hard' | 'extreme';

const t = ru.rules;

/** The generator types EquatableArray as unknown; on the wire it is a plain JSON array */
function list<T>(value: unknown, item: (x: unknown) => x is T): T[] {
  return Array.isArray(value) ? value.filter(item) : [];
}

const isNumber = (x: unknown): x is number => typeof x === 'number';
const isString = (x: unknown): x is string => typeof x === 'string';

/** Hours the example on the page counts dice for: a typical campaign */
const exampleHours = 12;

/** How many dice a game of these hours brings, by the rule in force (the engine's CompletionRoll.Count) */
export function diceFor(hours: number, rule: Ruleset['reward']['diceCount']): number {
  const raw = hours / rule.hoursPerDie;
  // Math.round goes half up, the engine half away from zero: equal for positive hours
  const rounded =
    rule.rounding === 'floor'
      ? Math.floor(raw)
      : rule.rounding === 'ceil'
        ? Math.ceil(raw)
        : Math.round(raw);
  return Math.min(rule.max, Math.max(rule.min, rounded));
}

export type DieRow = { difficulty: Difficulty; label: string; die: string };
export type BonusRow = { place: string; points: string };

export type RulesPage = {
  win: string[];
  roll: string[];
  reward: string[];
  dice: DieRow[];
  rewardAfter: string[];
  drop: string[];
  bonuses: BonusRow[];
  finish: string[];
};

/** The page's sentences for a ruleset; the deadline comes apart (it is the season's, not the ruleset's) */
export function rulesPage(rules: Ruleset): RulesPage {
  const { season, roll, reward, drop, finish, ranking, map, features } = rules;

  const tiebreakers = list(ranking.tiebreakers, isString)
    .map((x) => t.win.tiebreaker[x])
    .filter(isString);
  const win = [
    t.win.twoScores,
    ...(features.mapMode === 'linear' ? [t.win.map(map.linearLength)] : []),
    t.win.first,
    ...(finish.requireApprovalForFirst ? [t.win.firstApproval] : []),
    t.win.firstFrozen,
    t.win.others,
    t.win.nobody,
    ...(tiebreakers.length > 0 ? [t.win.tiebreakers(tiebreakers.join(t.win.then))] : []),
  ];

  const rollLines = [
    t.roll.wheel,
    ...(roll.choiceCount > 1 ? [t.roll.choice(roll.choiceCount)] : []),
    roll.freeRerollsPerRoll > 0 ? t.roll.free(roll.freeRerollsPerRoll) : t.roll.noFree,
    roll.rerollCost.kind === 'badEvent'
      ? t.roll.costBadEvent
      : t.roll.costCoins(roll.rerollCost.amount ?? 0),
    t.roll.alreadyPlayed,
    t.roll.busy,
    t.roll.active(season.maxActiveRunsPerPlayer),
    ...(season.maxUncheckedRuns == null ? [] : [t.roll.unchecked(season.maxUncheckedRuns)]),
  ];

  const count = reward.diceCount;
  const normal = reward.dieByDifficulty.normal.sides;
  const rewardLines = [
    t.reward.dice(count.hoursPerDie, count.rounding),
    t.reward.limits(count.min, count.max),
    t.reward.example(exampleHours, diceFor(exampleHours, count), normal),
  ];
  const difficulties: Difficulty[] = ['easy', 'normal', 'hard', 'extreme'];
  const dice = difficulties.map((difficulty) => {
    const rule = reward.dieByDifficulty[difficulty];
    return {
      difficulty,
      label: t.reward.difficulties[difficulty],
      die: rule.grantEvent ? t.reward.withEvent(rule.sides, rule.grantEvent) : `d${rule.sides}`,
    };
  });
  const rewardAfter = [
    t.reward.points,
    t.reward.proofFirst,
    t.reward.reject,
    ...(features.challenges && reward.challengeBonus.extraDice > 0
      ? [t.reward.challenge(reward.challengeBonus.extraDice)]
      : []),
    t.reward.coins(reward.coins.perHour, reward.coins.min),
  ];

  const penalty = drop.penaltyDice;
  const dropLines = [
    t.drop.after(roll.minPlayMinutesBeforeDrop),
    t.drop.penalty(penalty.count, penalty.sides, drop.affectsPoints, drop.affectsPosition),
    ...(drop.mandatoryEvent === 'bad' ? [t.drop.badEvent] : []),
    t.drop.floor,
    t.drop.tech(roll.techRerollWindowHours),
  ];

  // The first finisher holds no bonus: the list starts at the second place (the engine's Finishing)
  const byOrder = list(finish.bonusByOrder, isNumber);
  const bonuses = [
    ...byOrder.map((points, i) => ({
      place: t.finish.place(i + 2),
      points: t.finish.points(points),
    })),
    ...(finish.bonusAfterList > 0
      ? [
          {
            place: t.finish.after(byOrder.length + 2),
            points: t.finish.points(finish.bonusAfterList),
          },
        ]
      : []),
  ];
  const noBonus = byOrder.every((x) => x === 0) && finish.bonusAfterList === 0;
  const finishLines = [...(noBonus ? [t.finish.noBonus] : []), t.finish.later, t.finish.surplus];

  return {
    win,
    roll: rollLines,
    reward: rewardLines,
    dice,
    rewardAfter,
    drop: dropLines,
    bonuses: noBonus ? [] : bonuses,
    finish: finishLines,
  };
}

/** A changed field in words: «Максимум кубиков», «Бонус за финиш: 3-е место»; an unknown one by its path */
export function fieldName(path: string): string {
  const bonus = /^finish\.bonusByOrder\[(\d+)\]$/.exec(path);
  if (bonus) return t.bonusField(Number(bonus[1]) + 2);
  return t.fields[path] ?? path;
}

/** A value of the history in words: numbers as they are, yes and no, known words translated; absent is «нет» */
export function valueText(json: string | null): string {
  if (json === null || json === 'null') return t.history.none;
  if (json === 'true') return t.history.yes;
  if (json === 'false') return t.history.no;
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch {
    return json;
  }
  if (typeof value === 'number') return value.toLocaleString('ru-RU');
  if (typeof value === 'string') return t.values[value] ?? value;
  return json;
}
