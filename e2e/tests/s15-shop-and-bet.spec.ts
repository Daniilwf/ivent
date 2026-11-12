import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, rollAndStart, setUpSeason, signIn } from '../support/world.ts';

// Stage 4 (4.11c, D-447): the shop and a bet on the season screen. The admin turns items, the shop and bets on, loads
// the repository's content pack and gives Вася coins; Петя rolls a game and starts it. Вася rolls his shop — the lots
// come with their timer — and buys one: it lands in his inventory and the coins go. Then he bets on Петя's run: the
// sheet says the win before the press, the bet is listed as open and the stake leaves his coins.

const content = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'content');

function pack() {
  const read = (dir: string) =>
    readdirSync(join(content, dir))
      .filter((f) => f.endsWith('.json'))
      .sort()
      .map((f) => JSON.parse(readFileSync(join(content, dir, f), 'utf8')) as unknown);
  return { objects: read('items'), wheels: read('wheels') };
}

test('the shop and a bet: roll, buy a lot, bet on a run', async ({ browser }, testInfo) => {
  // Screenshots for the design review (docs/DESIGN_REVIEW.md, 4.11c), not compared
  const shot = (name: string) =>
    join(
      dirname(fileURLToPath(import.meta.url)),
      '..',
      '..',
      'test-artifacts',
      'design',
      'shop-bets',
      `${name}-${testInfo.project.name}.png`,
    );
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [vasya, petya] = world.players;
  if (!vasya || !petya) throw new Error('No players.');
  const admin = world.adminApi;

  const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
  await admin.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: { ...rules.ruleset.features, items: true, shop: true, bets: true },
    },
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/content/publish`, {
    expectedVersion: 0,
    pack: pack(),
    comment: 'Предметы для E2E',
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/players/${vasya.playerId}/adjust`, {
    comment: 'Монетки',
    coinsDelta: 100,
  });
  await rollAndStart(await apiOf(petya), world.seasonId);

  const page = await signIn(browser, vasya);
  const vasyaApi = await apiOf(vasya);
  const economy = () =>
    vasyaApi.get<Schemas['EconomyView']>(`/api/seasons/${world.seasonId}/economy`);
  const before = await economy();
  const start = before.coins;
  const rollPrice = before.shop?.price ?? 0;

  // The shop: no lots yet, the roll says its price
  const shop = page.getByTestId('shop');
  await expect(shop).toContainText('Лотов пока нет');
  const roll = shop.getByTestId('shop-roll');
  await expect(roll).toContainText('Ролл магазина за');
  await roll.click();

  // The lots come with their timer; a lot the engine sells has «Купить»
  await expect(shop.getByTestId('shop-lots')).toBeVisible();
  await expect(shop.getByTestId('shop-timer')).toContainText('Лоты исчезнут через');
  await shop.screenshot({ path: shot('shop-lots') });
  await page.screenshot({ path: shot('season') });
  const rolled = await economy();
  const lot = rolled.shop?.lots.find((l) => l.refusal === null);
  if (!lot) throw new Error('No lot for sale.');
  await shop.getByTestId(`buy-${String(lot.lot)}`).click();

  await expect(shop.getByRole('status')).toHaveText(`Куплено: «${lot.name}». Уже в инвентаре.`);
  await expect(shop.getByTestId(`lot-${String(lot.lot)}`)).toContainText('Куплено');
  await expect(page.getByTestId(`item-${lot.objectId}`)).toBeVisible();
  const bought = await economy();
  expect(bought.coins).toBe(start - rollPrice - lot.price);
  await expect(page.getByTestId('coins')).toHaveText(new RegExp(`^${String(bought.coins)} `));

  // The bet: Петя's run is open, the sheet says the win, the bet is listed open
  const bets = page.getByTestId('bets');
  await expect(bets).toContainText('Ставки');
  await bets.getByTestId(`bet-on-${petya.playerId}`).click();
  const sheet = page.getByTestId('bet-sheet');
  await expect(sheet).toBeVisible();
  await sheet.getByText(/^3 дня, ×/).click();
  const stake = sheet.getByLabel('Ставка в монетках');
  await stake.fill('2');
  await expect(sheet.getByTestId('bet-payout')).toContainText('Выигрыш, если успеет:');
  await page.screenshot({ path: shot('bet-sheet') });
  await sheet.getByTestId('bet-confirm').click();

  await expect(sheet).toBeHidden();
  await expect(bets.getByRole('status')).toHaveText('Ставка на игрока Петя принята.');
  await expect(bets.getByTestId('my-bets')).toContainText('Петя');
  await expect(bets.getByTestId('bet-runs')).toHaveCount(0);
  const after = await economy();
  expect(after.coins).toBe(bought.coins - 2);
  expect(after.bets?.bets[0]).toMatchObject({
    onPlayerId: petya.playerId,
    stake: 2,
    status: 'open',
  });
  await expect(page.getByTestId('coins')).toHaveText(new RegExp(`^${String(after.coins)} `));
  await bets.screenshot({ path: shot('bets-placed') });
  await shop.screenshot({ path: shot('shop-bought') });
});
