import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, confirmButton, live, openAdmin, setUpSeason, signIn } from '../support/world.ts';

// Stage 4 (4.13): the whole way of an item from the admin to another player's screen, three browsers. The admin turns
// items and the shop on and loads a pack from a file in «Контент» (its only item for sale is «Птичкерс»); Вася rolls
// his shop, buys the lot and uses it on Петя from his inventory; Петя, on his own page, sees his coins go without a
// reload, and the feed tells it. The parts are covered one by one in s14 (an item given by the admin) and s15 (a lot
// bought); this is them together, the pack loaded as the admin loads it.

const thief = {
  id: 'bird-thief',
  kind: 'item',
  name: 'Птичкерс',
  description: 'Укради у выбранного игрока 1d4 монеток.',
  rarity: 'common',
  price: 15,
  window: 'anytime',
  hostile: true,
  stackable: false,
  effect: {
    target: { selector: 'chosen', excludeSelf: true },
    actions: [
      { type: 'roll', dice: '1d4' },
      { type: 'changeResource', resource: 'coins', amount: '-$roll' },
      { type: 'changeResource', resource: 'coins', amount: '$roll', target: 'self' },
    ],
  },
};

test('a lot bought in the shop and used on another player, seen live by him', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [vasya, petya] = world.players;
  if (!vasya || !petya) throw new Error('No players.');
  const admin = world.adminApi;

  // Items and the shop on in the season's rules; the coins to shop with and to lose
  const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
  await admin.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, items: true, shop: true } },
  });
  for (const [player, coinsDelta] of [
    [vasya, 50],
    [petya, 10],
  ] as const)
    await admin.post(`/api/admin/seasons/${world.seasonId}/players/${player.playerId}/adjust`, {
      comment: 'Монетки',
      coinsDelta,
    });

  // The pack goes in from a file, as the admin loads it (the editor works on a computer)
  const vasyaPage = await signIn(browser, vasya);
  const petyaPage = await signIn(browser, petya);
  await expect(petyaPage.getByTestId('coins')).toHaveText(/^10 /);
  if (!testInfo.project.name.startsWith('phone')) {
    const adminPage = await signIn(browser, world.admin);
    await openAdmin(adminPage, world, 'content');
    await adminPage.getByTestId('content-import-file').setInputFiles({
      name: 'items.json',
      mimeType: 'application/json',
      buffer: Buffer.from(JSON.stringify({ objects: [thief], wheels: [] })),
    });
    await expect(adminPage.getByTestId('content-import-result')).toBeVisible(live);
    await adminPage.getByTestId('content-import-comment').fill('Птичкерс в магазин');
    await adminPage.getByTestId('content-import-publish').click();
    await confirmButton(adminPage, 'Опубликовать пакет').click();
    await expect(adminPage.getByTestId('content-list')).toContainText('Версия контента 1', live);
  } else {
    await admin.post(`/api/admin/seasons/${world.seasonId}/content/publish`, {
      expectedVersion: 0,
      pack: { objects: [thief], wheels: [] },
      comment: 'Птичкерс в магазин',
    });
  }

  // Вася: the shop appears with the mechanics, the roll gives the one lot, «Купить за 15»
  const shop = vasyaPage.getByTestId('shop');
  await expect(shop).toBeVisible(live);
  await shop.getByTestId('shop-roll').click();
  await expect(shop.getByTestId('shop-lots')).toBeVisible();
  await shop.getByTestId('buy-0').click();
  await expect(shop.getByRole('status')).toHaveText('Куплено: «Птичкерс». Уже в инвентаре.');

  // ... and uses it on Петя, the only target
  const item = vasyaPage.getByTestId('item-bird-thief');
  await expect(item).toBeVisible();
  await item.getByTestId('use-bird-thief').click();
  const sheet = vasyaPage.getByTestId('use-item');
  await expect(sheet.getByTestId(`target-${petya.playerId}`)).toBeVisible();
  await sheet.getByTestId(`target-${petya.playerId}`).click();
  await sheet.getByTestId('use-item-confirm').click();
  await expect(sheet).toBeHidden();
  await expect(vasyaPage.getByTestId('inventory')).toContainText('Использовано: «Птичкерс»');

  // Петя sees his coins go without a reload, by exactly what Вася got
  const petyaApi = await apiOf(petya);
  const stolen =
    10 -
    (await petyaApi.get<Schemas['EconomyView']>(`/api/seasons/${world.seasonId}/economy`)).coins;
  expect(stolen).toBeGreaterThanOrEqual(1);
  expect(stolen).toBeLessThanOrEqual(4);
  await expect(petyaPage.getByTestId('coins')).toHaveText(
    new RegExp(`^${String(10 - stolen)} `),
    live,
  );
});
