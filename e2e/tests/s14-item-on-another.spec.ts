import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, live, setUpSeason, signIn } from '../support/world.ts';

// Stage 4 (4.11b, D-441): an item on another player, both browsers live. The admin turns items on in the season's rules,
// loads the repository's content pack (content/items, content/wheels) and gives Вася «Птичкерс»; Петя has coins. Вася
// opens his inventory, picks Петя in the sheet of «Использовать» — the only target offered — and uses it: Вася sees it
// done and his coins come; Петя, on his own page, sees his coins go without reloading.

const content = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'content');

function pack() {
  const read = (dir: string) =>
    readdirSync(join(content, dir))
      .filter((f) => f.endsWith('.json'))
      .sort()
      .map((f) => JSON.parse(readFileSync(join(content, dir, f), 'utf8')) as unknown);
  return { objects: read('items'), wheels: read('wheels') };
}

test('an item on another player: the target is offered, the use is seen live by both', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [vasya, petya] = world.players;
  if (!vasya || !petya) throw new Error('No players.');
  const admin = world.adminApi;

  // Items on, the pack in, the item and the coins given
  const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
  await admin.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, items: true } },
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/content`, {
    pack: pack(),
    comment: 'Предметы для E2E',
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/players/${vasya.playerId}/inventory`, {
    giveObjectId: 'bird-thief',
    comment: 'Подарок',
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/players/${petya.playerId}/adjust`, {
    comment: 'Монетки',
    coinsDelta: 10,
  });

  const vasyaPage = await signIn(browser, vasya);
  const petyaPage = await signIn(browser, petya);
  await expect(petyaPage.getByTestId('coins')).toHaveText(/^10 /);
  await expect(petyaPage.getByTestId('inventory')).toContainText('В инвентаре пусто');

  // Вася: the inventory, «Использовать», the only target offered is Петя (Вася himself is not)
  const thief = vasyaPage.getByTestId('item-bird-thief');
  await expect(thief).toContainText('Птичкерс');
  await thief.getByTestId('use-bird-thief').click();
  const sheet = vasyaPage.getByTestId('use-item');
  await expect(sheet).toBeVisible();
  const targets = sheet.getByTestId('use-targets').getByRole('radio');
  await expect(targets).toHaveCount(1);
  // The only target is picked already
  await expect(targets.first()).toBeChecked();
  await sheet.getByTestId(`target-${petya.playerId}`).click();
  await sheet.getByTestId('use-item-confirm').click();

  // Вася sees it done, the item gone and the stolen coins
  await expect(sheet).toBeHidden();
  await expect(vasyaPage.getByTestId('inventory')).toContainText('«Птичкерс» использован.');
  await expect(vasyaPage.getByTestId('item-bird-thief')).toHaveCount(0);
  const economy = await (
    await apiOf(vasya)
  ).get<Schemas['EconomyView']>(`/api/seasons/${world.seasonId}/economy`);
  const stolen = economy.coins;
  expect(stolen).toBeGreaterThanOrEqual(1);
  expect(stolen).toBeLessThanOrEqual(4);
  await expect(vasyaPage.getByTestId('coins')).toHaveText(new RegExp(`^${String(stolen)} `));

  // Петя sees his coins go without a reload (the hub)
  await expect(petyaPage.getByTestId('coins')).toHaveText(
    new RegExp(`^${String(10 - stolen)} `),
    live,
  );
});
