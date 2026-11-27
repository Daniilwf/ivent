import type { components } from '../api/schema.ts';

type Schemas = components['schemas'];

const seasonId = '5ea50000-0000-0000-0000-000000000001';

export function required<T>(value: T | null | undefined): T {
  if (value == null) throw new Error('The preview fixture is incomplete.');
  return value;
}

export const previewPlayerId = '10000000-0000-0000-0000-000000000001';
export const otherPreviewPlayerId = '10000000-0000-0000-0000-000000000002';
export const previewPath = (playerId = previewPlayerId, id = seasonId) =>
  `/admin/seasons/${id}/players/${playerId}/preview`;
export const previewApiPath = (playerId = previewPlayerId, id = seasonId) =>
  `/api/admin/seasons/${id}/players/${playerId}/preview`;

/** A real API-shaped private snapshot; tests vary only the data relevant to their scenario. */
export function playerPreviewFixture(
  overrides: Partial<Schemas['PlayerPreviewView']> = {},
): Schemas['PlayerPreviewView'] {
  const season: Schemas['SeasonView'] = {
    id: seasonId,
    name: 'Осенний сезон',
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'c1', type: 'empty' },
      { id: 'finish', type: 'finish' },
    ],
    edges: [],
    zones: [],
    mapMode: 'linear',
    lastSequence: 3,
    players: [
      {
        id: previewPlayerId,
        name: 'Вася',
        cellId: 'start',
        points: 7,
        token: 0,
        phase: 'rolling',
        finishOrder: null,
        avatar: null,
      },
    ],
    leaderboard: [
      {
        playerId: previewPlayerId,
        place: 1,
        points: 7,
        cellsToFinish: 2,
        isFirst: false,
        provisional: false,
      },
    ],
    me: {
      playerId: previewPlayerId,
      phase: 'rolling',
      offer: {
        id: '60000000-0000-0000-0000-000000000001',
        title: 'Celeste',
        hours: 8,
        cover: null,
        marks: [],
        conditions: [],
      },
      choice: null,
      activeRun: null,
      lastCompleted: null,
      nextReroll: null,
      manualEffects: [],
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
      challengesEnabled: false,
      roll: null,
      unchecked: null,
      finish: null,
      coins: 12,
      waitingEvents: [],
      waitingConditions: [],
      checks: [],
    },
    economy: { items: false, shop: false, bets: false },
  };
  return { playerId: previewPlayerId, playerName: 'Вася', season, economy: null, ...overrides };
}

/** Dense finished state for viewport review, with every field kept in the generated API shape. */
export function finishedPlayerPreviewFixture(): Schemas['PlayerPreviewView'] {
  const value = playerPreviewFixture();
  value.playerName = 'Александр с очень длинным игровым именем';
  value.season.name = 'Осенний сезон с длинным названием';
  value.season.status = 'finished';
  value.season.economy = { items: true, shop: false, bets: false };
  value.season.deadline = '2026-11-25T18:00:00Z';
  value.season.players = Array.from({ length: 16 }, (_, index) => ({
    ...required(value.season.players[0]),
    id:
      index === 0 ? previewPlayerId : `20000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
    name: index === 0 ? value.playerName : `Участник ${String(index + 1)}`,
    cellId: index === 0 ? 'finish' : 'c1',
    points: 120 - index,
    phase: 'idle',
    finishOrder: index === 0 ? 1 : null,
  }));
  value.season.leaderboard = value.season.players.map((player, index) => ({
    ...required(value.season.leaderboard[0]),
    playerId: player.id,
    name: player.name,
    place: index + 1,
    points: player.points,
    isFirst: index === 0,
    cellsToFinish: index === 0 ? 0 : 1,
  }));
  const me = required(value.season.me);
  me.phase = 'idle';
  me.coins = 112;
  me.offer = null;
  me.finish = { order: 1, frozen: true };
  me.lastCompleted = {
    id: '70000000-0000-0000-0000-000000000001',
    game: {
      id: '60000000-0000-0000-0000-000000000002',
      title: 'Hollow Knight',
      hours: 20,
      cover: null,
    },
    difficulty: 'hard',
    dice: [
      { value: 8, sides: 8 },
      { value: 7, sides: 8 },
    ],
    challengeDice: [{ value: 6, sides: 6 }],
    total: 21,
    review: { rating: 9, text: 'Долгая дорога к финишу' },
    status: 'completed',
    conditions: [
      {
        objectId: 'hard-mode',
        name: 'Пройти на высокой сложности',
        difficultyAtLeast: 'hard',
        difficultyAtMost: null,
        kind: 'bad',
        hostile: false,
      },
    ],
    conditionCheck: { met: true, waived: false },
    proof: {
      status: 'approved',
      links: ['https://example.com/final-credits'],
      files: [],
      note: 'Полные титры и финальная сцена',
      comment: 'Пруф проверен',
      witnessId: required(value.season.players[1]).id,
    },
  };
  value.economy = {
    coins: 112,
    inventoryLimit: 16,
    shop: null,
    bets: null,
    serverNow: '2026-11-27T01:00:00Z',
    items: Array.from({ length: 16 }, (_, index) => ({
      instanceId: `90000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
      objectId: `item-${String(index)}`,
      kind: 'item',
      name: `Предмет с длинным названием ${String(index + 1)}`,
      description: 'Подробное описание предмета с условиями применения и результатом.',
      rarity: 'epic',
      window: 'beforeRoll',
      hostile: false,
      usesLeft: 2,
      runsLeft: null,
      expiresAt: null,
      fromPlayer: index % 2 === 0 ? 'Участник 2' : null,
      usable: true,
      refusal: null,
      choosesTarget: false,
      targets: [],
      steps: [],
    })),
  };
  return value;
}
