import type { Schemas } from '../api/client';
import { difficultyValues } from '../api/schema';
import { ru } from '../i18n/ru';

type Difficulty = (typeof difficultyValues)[number];

// H7: the rules page is built from the season's ruleset, so every number on it is the one in force (SPEC «Правила на
// сайте»). This module turns the ruleset into the page's sentences and the history's «было/стало» into words.

export type Ruleset = Schemas['Ruleset'];

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
  // Rounded to a billionth first: binary fractions (12 / 2.4) must not fall under a whole number the engine's decimal hits
  const raw = Math.round((hours / rule.hoursPerDie) * 1e9) / 1e9;
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
  /** Coins, items, the shop and bets — empty while all their mechanics are off (S4-1) */
  economy: string[];
};

/** The page's sentences for a ruleset; the deadline comes apart (it is the season's, not the ruleset's) */
export function rulesPage(rules: Ruleset): RulesPage {
  const { season, roll, reward, drop, finish, ranking, map, features, economy, bets } = rules;

  const tiebreakers = list(ranking.tiebreakers, isString)
    .map((x) => t.win.tiebreaker[x])
    .filter(isString);
  const win = [
    t.win.twoScores,
    ...(features.mapMode === 'linear' ? [t.win.map(map.linearLength)] : []),
    t.win.first,
    ...(finish.requireApprovalForFirst
      ? [t.win.firstApproval, t.win.firstFrozenApproved]
      : [t.win.firstFrozen]),
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
  const dice = difficultyValues.map((difficulty) => {
    const rule = reward.dieByDifficulty[difficulty];
    return {
      difficulty,
      label: difficulty === 'normal' ? t.reward.normalOrNone : ru.difficulty[difficulty],
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
    t.reward.coins(reward.coins.perHour, reward.coins.min, count.max * count.hoursPerDie),
  ];

  const penalty = drop.penaltyDice;
  const wishTags = list(roll.wishRerollTags, isString);
  const dropLines = [
    t.drop.after(roll.minPlayMinutesBeforeDrop),
    t.drop.penalty(penalty.count, penalty.sides, drop.affectsPoints, drop.affectsPosition),
    ...(drop.consecutiveExtraDice
      ? [t.drop.consecutive(drop.consecutiveExtraDice, penalty.sides)]
      : []),
    ...(drop.mandatoryEvent === 'bad' ? [t.drop.badEvent] : []),
    t.drop.floor,
    t.drop.tech(roll.techRerollWindowHours),
    ...(wishTags.length > 0 ? [t.drop.wish(wishTags.join(', '), roll.techRerollWindowHours)] : []),
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

  const e = t.economy;
  const resets = list(economy.shop.resetOn, isString)
    .map((x) => e.resetOn[x])
    .filter(isString);
  const economyLines = [
    ...(features.items ? [e.inventory(economy.inventoryLimit)] : []),
    ...(features.items || features.bets ? [e.debt] : []),
    ...(features.shop
      ? [
          e.shop(economy.shop.rollCost, economy.shop.lotsPerRoll, economy.shop.lotLifetimeMinutes),
          ...(economy.shop.rerollCostStep > 0
            ? [
                e.shopStep(
                  economy.shop.rerollCostStep,
                  economy.shop.rollCost,
                  resets.length > 0 ? `${resets.join(' или ')} ${e.resetOf}` : '',
                ),
              ]
            : []),
        ]
      : []),
    ...(features.bets
      ? [
          e.bets(bets.windowHoursAfterRoll, orList(list(bets.deadlineOptionsDays, isNumber))),
          e.betLimits(bets.maxStake, bets.maxOpenBetsPerPlayer),
          e.payout(payoutSteps(bets.payoutByHoursPerDay).join(', ')),
          e.betLost,
        ]
      : []),
  ];

  return {
    economy: economyLines,
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

type PayoutStep = { upTo: number | null; multiplier: number };
const isStep = (x: unknown): x is PayoutStep =>
  typeof x === 'object' &&
  x !== null &&
  typeof (x as PayoutStep).multiplier === 'number' &&
  ((x as PayoutStep).upTo === null || typeof (x as PayoutStep).upTo === 'number');

/** The bet coefficients by the hours per day: «до 1 ч — ×1,2» */
function payoutSteps(value: unknown): string[] {
  return list(value, isStep).map((s) => t.economy.step(s.upTo, s.multiplier));
}

/** «1, 3 или 7» */
function orList(values: number[]): string {
  const words = values.map((v) => v.toLocaleString('ru-RU'));
  return words.length < 2
    ? words.join('')
    : `${words.slice(0, -1).join(', ')} или ${words.at(-1) ?? ''}`;
}

/** A changed field in words: «Максимум кубиков», «Бонус за финиш: 3-е место»; an unknown one by its path */
export function fieldName(path: string): string {
  const bonus = /^finish\.bonusByOrder\[(\d+)\]$/.exec(path);
  if (bonus) return t.bonusField(Number(bonus[1]) + 2);
  const payout = /^bets\.payoutByHoursPerDay\[(\d+)\]\.(\w+)$/.exec(path);
  if (payout) return t.payoutField(Number(payout[1]) + 1, payout[2] ?? '');
  return t.fields[path] ?? path;
}

/** A value of the history in words: numbers as they are, yes and no, known words translated; absent is «нет» */
export function valueText(json: string | null): string {
  if (json === null || json === 'null') return t.values.none ?? String(json);
  if (json === 'true') return t.history.yes;
  if (json === 'false') return t.values.none ?? json;
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
