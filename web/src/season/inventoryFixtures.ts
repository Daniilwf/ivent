import type { Schemas } from '../api/client';

// The inventory as the server gives it, for the tests and the styleguide (4.11a): a hostile item with two targets, an
// effect another player put on me.

type Item = Schemas['InventoryItemView'];

export function itemFixture(over: Partial<Item> = {}): Item {
  return {
    instanceId: 'i1000000-0000-0000-0000-000000000001',
    objectId: 'bird-thief',
    kind: 'item',
    name: 'Птичкерс',
    description: 'Укради у выбранного игрока 1d4 монеток.',
    rarity: 'common',
    window: 'anytime',
    hostile: true,
    usesLeft: null,
    runsLeft: null,
    expiresAt: null,
    fromPlayer: null,
    usable: true,
    refusal: null,
    choosesTarget: true,
    targets: [
      { playerId: '10000000-0000-0000-0000-000000000002', name: 'Петя', token: 1 },
      { playerId: '10000000-0000-0000-0000-000000000003', name: 'Коля', token: 2 },
    ],
    steps: [],
    ...over,
  };
}

export function economyFixture(over: Partial<Schemas['EconomyView']> = {}): Schemas['EconomyView'] {
  return {
    coins: 7,
    inventoryLimit: 5,
    shop: null,
    bets: null,
    items: [
      itemFixture(),
      itemFixture({
        instanceId: 'i1000000-0000-0000-0000-000000000002',
        objectId: 'orange',
        name: 'Апельсин',
        description: 'Сдвинься на 1 клетку вперёд.',
        hostile: false,
        choosesTarget: false,
        targets: [],
      }),
      itemFixture({
        instanceId: 'i1000000-0000-0000-0000-000000000003',
        objectId: 'curse-effect',
        kind: 'effect',
        name: 'Проклятие',
        description: 'Следующий бросок за прохождение уменьшится на 1d6, но не ниже 1.',
        rarity: null,
        window: null,
        runsLeft: 1,
        fromPlayer: 'Петя',
        usable: false,
        refusal: 'item.notAnItem',
        choosesTarget: false,
        targets: [],
      }),
    ],
    ...over,
  };
}

// The shop and the bets as the server gives them (4.11c): three lots for 15 minutes, one bought; a run open for a bet
// and my bets in every status.

type Shop = Schemas['ShopView'];
type Bets = Schemas['BetsView'];

export const fixtureNow = new Date('2026-11-11T12:00:00Z');

export function shopFixture(over: Partial<Shop> = {}): Shop {
  return {
    payment: 'coins',
    price: 6,
    freeRolls: 0,
    refusal: null,
    expiresAt: new Date(fixtureNow.getTime() + 12 * 60_000 + 34_000).toISOString(),
    lots: [
      {
        lot: 0,
        objectId: 'orange',
        name: 'Апельсин',
        description: 'Сдвинься на 1 клетку вперёд.',
        rarity: 'common',
        price: 4,
        sold: false,
        refusal: null,
      },
      {
        lot: 1,
        objectId: 'lucky-die',
        name: 'Счастливый кубик',
        description: 'Следующий бросок за прохождение получает +1 кубик.',
        rarity: 'legendary',
        price: 15,
        sold: false,
        refusal: 'roll.notEnoughCoins',
      },
      {
        lot: 2,
        objectId: 'bird-thief',
        name: 'Птичкерс',
        description: 'Укради у выбранного игрока 1d4 монеток.',
        rarity: 'epic',
        price: 8,
        sold: true,
        refusal: 'shop.lotSold',
      },
    ],
    ...over,
  };
}

export const petyaId = '10000000-0000-0000-0000-000000000002';
export const kolyaId = '10000000-0000-0000-0000-000000000003';

export function betsFixture(over: Partial<Bets> = {}): Bets {
  return {
    deadlineOptionsDays: [1, 3, 7],
    maxStake: 10,
    refusal: null,
    openBets: 1,
    maxOpenBets: 3,
    stakeLimit: 7,
    runs: [
      {
        playerId: petyaId,
        name: 'Петя',
        token: 1,
        runId: 'r1000000-0000-0000-0000-000000000001',
        gameId: 'g1000000-0000-0000-0000-000000000001',
        gameTitle: 'Silent Hill 2',
        hours: 6,
        closesAt: '2026-11-12T09:30:00Z',
        options: [
          { days: 1, multiplier: 3 },
          { days: 3, multiplier: 2 },
          { days: 7, multiplier: 1.2 },
        ],
      },
    ],
    bets: [
      {
        betId: 'b1000000-0000-0000-0000-000000000001',
        onPlayerId: kolyaId,
        onPlayer: 'Коля',
        runId: 'r1000000-0000-0000-0000-000000000002',
        placedAt: '2026-11-10T10:00:00Z',
        deadline: '2026-11-13T10:00:00Z',
        stake: 5,
        multiplier: 2,
        status: 'open',
        payout: 0,
        gameTitle: 'Hollow Knight',
      },
      {
        betId: 'b1000000-0000-0000-0000-000000000002',
        onPlayerId: petyaId,
        onPlayer: 'Петя',
        runId: 'r1000000-0000-0000-0000-000000000003',
        placedAt: '2026-11-01T10:00:00Z',
        deadline: '2026-11-04T10:00:00Z',
        stake: 3,
        multiplier: 3,
        status: 'won',
        payout: 9,
        gameTitle: 'Celeste',
      },
      {
        betId: 'b1000000-0000-0000-0000-000000000003',
        onPlayerId: kolyaId,
        onPlayer: 'Коля',
        runId: 'r1000000-0000-0000-0000-000000000004',
        placedAt: '2026-10-25T10:00:00Z',
        deadline: '2026-10-26T10:00:00Z',
        stake: 2,
        multiplier: 1.2,
        status: 'lost',
        payout: 0,
        gameTitle: 'Gothic',
      },
    ],
    ...over,
  };
}
