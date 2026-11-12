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
