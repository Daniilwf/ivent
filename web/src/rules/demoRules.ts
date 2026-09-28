import type { Schemas } from '../api/client';

// Demo rules for the styleguide and the tests: the default ruleset of docs/ruleset.default.json, changed twice

export const demoRuleset: Schemas['Ruleset'] = {
  version: 1,
  features: {
    mapMode: 'linear',
    shop: false,
    items: false,
    events: false,
    bets: false,
    polls: false,
    achievements: false,
    weeklyChallenge: false,
    partnerBoard: false,
    reactions: false,
    comments: false,
    gallery: false,
    challenges: false,
  },
  season: {
    timezone: 'Europe/Moscow',
    maxActiveRunsPerPlayer: 1,
    inactiveHintDays: 3,
    maxUncheckedRuns: 2,
  },
  roll: {
    choiceCount: 1,
    freeRerollsPerRoll: 1,
    rerollCost: { kind: 'coins', amount: 5 },
    techRerollWindowHours: 48,
    minPlayMinutesBeforeDrop: 60,
    emptyPoolFallback: 'dropZoneFilter',
    lastDaysLengthFilter: {
      enabled: false,
      steps: [
        { daysBeforeDeadline: 5, maxHours: 10 },
        { daysBeforeDeadline: 2, maxHours: 4 },
      ],
    },
    wishRerollTags: [],
  },
  reward: {
    diceCount: { hoursPerDie: 3, rounding: 'nearest', min: 1, max: 10 },
    dieByDifficulty: {
      easy: { sides: 2 },
      normal: { sides: 4 },
      hard: { sides: 6 },
      extreme: { sides: 6, grantEvent: 'good' },
    },
    challengeBonus: { extraDice: 1 },
    unmetConditionPolicy: 'noDiceKeepCoins',
    coins: { perHour: 1, min: 3 },
    coop: { pointsShare: 0.5, roundUpFor: 'roller' },
  },
  drop: {
    penaltyDice: { count: 2, sides: 4 },
    affectsPoints: true,
    affectsPosition: true,
    mandatoryEvent: 'bad',
    consecutiveExtraDice: 1,
  },
  finish: { requireApprovalForFirst: true, bonusByOrder: [10, 8, 6, 4], bonusAfterList: 2 },
  ranking: { tiebreakers: ['completedRuns', 'earliestFinalScore'] },
  map: { linearLength: 70 },
  economy: {
    allowNegativeCoins: true,
    inventoryLimit: 5,
    rarityWeights: { common: 70, epic: 25, legendary: 5 },
    shop: {
      lotsPerRoll: 3,
      lotLifetimeMinutes: 15,
      rollCost: 5,
      rerollCostStep: 5,
      resetOn: ['runCompleted', 'runDropped'],
    },
  },
  effects: {
    maxChainDepth: 3,
    maxEventsPerCommand: 50,
    hostileCap: { enabled: false, maxActive: 1 },
    attacksOnlyOnHigherPoints: false,
  },
  interactions: { inviteTtlHours: 24 },
  bets: {
    maxStake: 10,
    maxOpenBetsPerPlayer: 3,
    windowHoursAfterRoll: 24,
    deadlineOptionsDays: [1, 3, 7],
    payoutByHoursPerDay: [
      { upTo: 1, multiplier: 1.2 },
      { upTo: 3, multiplier: 2.0 },
      { upTo: null, multiplier: 3.0 },
    ],
  },
  social: {
    reactions: ['🔥', '😂', '💀', '👏', '🤡', '❤️'],
    galleryUploadsPerDay: 10,
    commentsPerDay: 100,
    spectatorsCanVote: false,
  },
  nominations: {
    auto: ['mostHours', 'mostRuns', 'unluckiestDice', 'mostDrops', 'mostAttacks'],
    voted: ['bestReview', 'gameOfSeason', 'funniestMoment'],
  },
  weeklyChallenge: { defaultRewardCoins: 10 },
};

export const demoRules: Schemas['RulesView'] = {
  version: 3,
  ruleset: demoRuleset,
  deadline: '2026-12-20T20:59:00Z',
  history: [
    {
      version: 3,
      at: '2026-10-14T18:30:00Z',
      authorId: 'a0000000-0000-0000-0000-000000000001',
      authorName: 'Админ',
      changes: [
        { path: 'finish.bonusByOrder[0]', before: '12', after: '10' },
        { path: 'roll.rerollCost.amount', before: '3', after: '5' },
      ],
    },
    {
      version: 2,
      at: '2026-10-05T09:00:00Z',
      authorId: 'a0000000-0000-0000-0000-000000000001',
      authorName: 'Админ',
      changes: [{ path: 'season.maxUncheckedRuns', before: 'null', after: '2' }],
    },
    {
      version: 1,
      at: '2026-10-01T12:00:00Z',
      authorId: null,
      authorName: null,
      changes: [],
    },
  ],
};
