import { readFileSync } from 'node:fs';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  goToAdminSection,
  openAdmin,
  setUpSeason,
  signInHere,
  type Player,
  type World,
} from '../support/world.ts';

async function landAttack(world: World, attacker: Player, recipient: Player) {
  const admin = world.adminApi;
  const adminSeason = `/api/admin/seasons/${world.seasonId}`;
  const season = `/api/seasons/${world.seasonId}`;
  const { ruleset, version } = await admin.get<Schemas['RulesView']>(`${season}/rules`);
  await admin.put(`${adminSeason}/rules`, {
    expectedVersion: version,
    ruleset: { ...ruleset, features: { ...ruleset.features, items: true } },
  });
  const item: unknown = JSON.parse(
    readFileSync(new URL('../../content/items/bird-thief.json', import.meta.url), 'utf8'),
  );
  await admin.post(`${adminSeason}/content/publish`, {
    expectedVersion: 0,
    pack: { objects: [item], wheels: [] },
    comment: 'Предмет для проверки статистики',
  });
  await admin.post(`${adminSeason}/players/${attacker.playerId}/inventory`, {
    giveObjectId: 'bird-thief',
    comment: 'Предмет для атаки',
  });
  await admin.post(`${adminSeason}/players/${recipient.playerId}/adjust`, {
    coinsDelta: 10,
    comment: 'Монетки для атаки',
  });

  const player = await apiOf(attacker);
  try {
    const inventory = await player.get<Schemas['EconomyView']>(`${season}/economy`);
    const held = inventory.items?.find((entry) => entry.objectId === 'bird-thief');
    if (!held) throw new Error('The attacker did not receive the item.');
    const result = await player.post(`${season}/items/${held.instanceId}/use`, {
      targetPlayerId: recipient.playerId,
    });
    expect(result.events.filter((event) => event.type === 'hostile-received')).toHaveLength(1);
  } finally {
    await player.dispose();
  }
}

async function capture(page: Page, info: TestInfo, name: string) {
  const viewport =
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
  expect(page.viewportSize()).toEqual(viewport);
  await page.evaluate(() => document.fonts.ready);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    viewport.width,
  );
  const path = info.outputPath(`${name}.png`);
  await page.screenshot({ path, scale: 'css' });
  await info.attach(name, { path, contentType: 'image/png' });
}

test('the admin compares attacks and own activity in the active and archived seasons', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Яков', 'Анна'] });
  const [attacker, recipient] = world.players;
  try {
    if (!attacker || !recipient) throw new Error('The statistics fixture needs two players.');
    await landAttack(world, attacker, recipient);

    const archiveId = crypto.randomUUID();
    const archiveName = `Архив ${world.seasonName}`;
    const archive = `/api/admin/seasons/${archiveId}`;
    await world.adminApi.post('/api/admin/seasons', { seasonId: archiveId, name: archiveName });
    await world.adminApi.post(`${archive}/players`, { userId: recipient.id });
    for (const to of ['active', 'closing', 'finished', 'archived'] as const) {
      await world.adminApi.post(`${archive}/status`, { to });
    }

    await page.goto('/');
    await signInHere(page, world.admin);
    await openAdmin(page, world, 'statistics');
    const heading = page.getByRole('heading', { name: 'Статистика', level: 1 });
    await expect(heading).toBeFocused();
    await expect(
      page.getByText(`Сезон: ${world.seasonName} (идёт)`, { exact: true }),
    ).toBeVisible();

    const table = page.getByRole('table', { name: 'Статистика' });
    for (const name of ['Игрок', 'Нанесено атак', 'Получено атак', 'Последнее действие']) {
      await expect(table.getByRole('columnheader', { name, exact: true })).toBeAttached();
    }
    const rows = table.getByRole('rowheader');
    await expect(rows).toHaveText([recipient.name, attacker.name]);

    const attack = table.getByRole('row').filter({
      has: page.getByRole('rowheader', { name: attacker.name, exact: true }),
    });
    const received = table.getByRole('row').filter({
      has: page.getByRole('rowheader', { name: recipient.name, exact: true }),
    });
    await expect(attack.getByRole('cell').nth(0)).toHaveText(/(?:^|\D)1$/);
    await expect(attack.getByRole('cell').nth(1)).toHaveText(/(?:^|\D)0$/);
    await expect(attack.locator('time')).toHaveText(/\d{2}:\d{2} МСК/);
    await expect(attack.locator('time')).toHaveAttribute('datetime', /^\d{4}-\d{2}-\d{2}T/);
    await expect(received.getByRole('cell').nth(0)).toHaveText(/(?:^|\D)0$/);
    await expect(received.getByRole('cell').nth(1)).toHaveText(/(?:^|\D)1$/);
    await expect(received.getByText('Пока без действий')).toBeVisible();
    await page
      .getByRole('combobox', { name: 'Порядок' })
      .selectOption({ label: 'По нанесённым атакам' });
    await expect(rows).toHaveText([attacker.name, recipient.name]);
    await capture(page, info, 'statistics-active');

    await page.getByRole('link', { name: 'Сменить сезон', exact: true }).click();
    await expect(page).toHaveURL(/\/admin\/season$/);
    const open = page.getByTestId(`season-open-${archiveId}`);
    await expect(open).toBeVisible();
    await open.focus();
    await open.press('Enter');
    await expect(page.getByTestId('season-status')).toContainText(archiveName);
    await goToAdminSection(page, 'statistics');
    await expect(heading).toBeFocused();
    await expect(page.getByText(`Сезон: ${archiveName} (в архиве)`, { exact: true })).toBeVisible();
    await expect(rows).toHaveText([recipient.name]);
    await expect(received.getByRole('cell').nth(0)).toHaveText(/(?:^|\D)0$/);
    await expect(received.getByRole('cell').nth(1)).toHaveText(/(?:^|\D)0$/);
    await expect(received.getByText('Пока без действий')).toBeVisible();
    await capture(page, info, 'statistics-archive');
  } finally {
    await world.adminApi.dispose();
  }
});

test('the admin retries a failed statistics read without changing the selected season', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Лиса'] });
  try {
    await page.route(
      `**/api/admin/seasons/${world.seasonId}/statistics`,
      (route) => route.fulfill({ status: 503, json: {} }),
      { times: 1 },
    );
    await page.goto('/');
    await signInHere(page, world.admin);
    await openAdmin(page, world, 'statistics');
    const error = page.getByRole('alert');
    await expect(error).toBeVisible();
    await expect(page.getByRole('table')).toHaveCount(0);
    await capture(page, info, 'statistics-error');
    await error.getByRole('button', { name: 'Попробовать ещё раз' }).click();
    await expect(page.getByRole('table').getByRole('rowheader', { name: 'Лиса' })).toBeVisible();
    await expect(error).toHaveCount(0);
    await expect(
      page.getByText(`Сезон: ${world.seasonName} (идёт)`, { exact: true }),
    ).toBeVisible();
  } finally {
    await world.adminApi.dispose();
  }
});
