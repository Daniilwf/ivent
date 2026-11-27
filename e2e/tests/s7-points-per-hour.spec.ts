import { expect, test, type Page, type TestInfo } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  complete,
  live,
  openAdmin,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signInHere,
} from '../support/world.ts';

const number = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 });

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

test('the admin sees credited completion points per corrected hour without unrelated balance edits', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Лиса', 'Барсук'] });
  const owner = world.players[0];
  if (!owner) throw new Error('The efficiency scenario needs a player.');
  const actor = await apiOf(owner);
  const admin = `/api/admin/seasons/${world.seasonId}`;
  try {
    await rollAndStart(actor, world.seasonId);
    await complete(actor, world.seasonId);
    const completed = (await seasonOf(actor, world.seasonId)).me?.lastCompleted;
    if (!completed) throw new Error('The completion is missing.');
    await world.adminApi.post(`${admin}/runs/${completed.id}/hours`, {
      hours: 3,
      comment: 'Исправленная длина для отчёта очков в час',
    });
    const corrected = (await seasonOf(actor, world.seasonId)).me?.lastCompleted;
    if (!corrected) throw new Error('The corrected completion is missing.');
    expect(corrected.game.hours).toBe(3);
    expect(corrected.total).toBeGreaterThan(0);

    const pending = await world.adminApi.get<Schemas['AdminSeasonStatisticsView']>(
      `${admin}/statistics`,
    );
    const pendingOwner = pending.players.find((player) => player.playerId === owner.playerId);
    expect(pendingOwner?.completedRunHours).toBe(0);
    expect(pendingOwner?.completedRunPoints).toBe(0);
    expect(pendingOwner?.pointsPerHour).toBeNull();

    await page.goto('/');
    await signInHere(page, world.admin);
    await openAdmin(page, world, 'statistics');
    const table = page.getByRole('table', { name: 'Статистика' });
    const ownerRow = table.getByRole('row').filter({
      has: page.getByRole('rowheader', { name: owner.name, exact: true }),
    });
    await expect(ownerRow.getByText('Нет зачтённых часов', { exact: true })).toBeVisible();
    await capture(page, info, 'points-per-hour-pending');

    await world.adminApi.post(`${admin}/runs/${corrected.id}/approve`, {
      comment: 'Прохождение зачтено для проверки метрики',
    });
    await world.adminApi.post(`${admin}/players/${owner.playerId}/adjust`, {
      pointsDelta: 1000,
      comment: 'Отдельная правка баланса не является очками прохождения',
    });
    const report = await world.adminApi.get<Schemas['AdminSeasonStatisticsView']>(
      `${admin}/statistics`,
    );
    const measured = report.players.find((player) => player.playerId === owner.playerId);
    expect(measured?.completedRunPoints).toBe(corrected.total);
    expect(measured?.completedRunHours).toBe(3);
    expect(measured?.pointsPerHour).toBeCloseTo(corrected.total / 3, 10);

    await expect(
      ownerRow.getByText(number.format(corrected.total / 3), { exact: true }),
    ).toBeVisible(live);
    await expect(
      ownerRow.getByText(
        new RegExp(`^${number.format(corrected.total)} (?:очко|очка|очков) за 3 часа$`),
        { exact: true },
      ),
    ).toBeVisible();
    await expect(ownerRow.getByText('Нет зачтённых часов', { exact: true })).toHaveCount(0);
    const order = page.getByRole('combobox', { name: 'Порядок' });
    await order.selectOption('pointsPerHour');
    await expect(table.getByRole('rowheader').first()).toHaveText(owner.name);
    await expect(
      table.getByRole('columnheader', { name: 'Очки в час', exact: true }),
    ).toHaveAttribute('aria-sort', 'descending');
    await capture(page, info, 'points-per-hour-credited');
    const board = await seasonOf(actor, world.seasonId);
    expect(
      board.players.find((player) => player.id === owner.playerId)?.points,
    ).toBeGreaterThanOrEqual(1000);
  } finally {
    await actor.dispose();
    await world.adminApi.dispose();
  }
});

test('the efficiency column stays readable for dense, zero, missing and loading states', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Контрольный игрок'] });
  const players = Array.from({ length: 16 }, (_, index) => {
    let points = index < 2 ? 0 : 100 + index;
    let hours = index === 0 ? 0 : index + 1.25;
    if (index === 13) {
      points = 4;
      hours = 1e29;
    }
    if (index === 14) {
      points = 8;
      hours = 1e-28;
    }
    if (index === 15) {
      points = 1_234_567_890;
      hours = 1000;
    }
    return {
      playerId: crypto.randomUUID(),
      name: `Игрок с длинным именем для проверки переноса ${String(index + 1)}`,
      attacksMade: index,
      attacksReceived: 15 - index,
      lastActionAt: index % 2 === 0 ? null : '2026-10-12T08:23:00Z',
      completedRunPoints: points,
      completedRunHours: hours,
      pointsPerHour: hours === 0 ? null : points / hours,
    };
  }) satisfies Schemas['AdminSeasonStatisticsView']['players'];
  let state: 'dense' | 'error' | 'loading' | 'empty' = 'dense';
  let release: (() => void) | undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  try {
    await page.route(`**/api/admin/seasons/${world.seasonId}/statistics`, async (route) => {
      if (state === 'error') {
        await route.fulfill({ status: 503, json: {} });
        return;
      }
      if (state === 'loading') await gate;
      await route.fulfill({ status: 200, json: { players: state === 'dense' ? players : [] } });
    });
    await page.goto('/');
    await signInHere(page, world.admin);
    await openAdmin(page, world, 'statistics');
    const table = page.getByRole('table', { name: 'Статистика' });
    await expect(table.getByRole('rowheader')).toHaveCount(16);
    const order = page.getByRole('combobox', { name: 'Порядок' });
    await order.focus();
    await page.keyboard.press('Home');
    for (let step = 0; step < 3; step++) await page.keyboard.press('ArrowDown');
    await expect(order).toHaveValue('pointsPerHour');
    await expect(order).toBeFocused();
    await expect(table.getByRole('rowheader').first()).toHaveText(players[14]?.name ?? '');
    await expect(table.getByRole('rowheader').last()).toHaveText(players[0]?.name ?? '');
    await expect(table.getByText('8E28', { exact: true })).toBeVisible();
    await expect(table.getByText('8 очков за 1E-28 часа', { exact: true })).toBeVisible();
    await expect(table.getByText('4 очка за 1E29 часов', { exact: true })).toBeAttached();
    await expect(table.getByText('<0,01', { exact: true })).toBeAttached();
    await expect(table.getByText('0 очков за 2,25 часа', { exact: true })).toBeAttached();
    await capture(page, info, 'points-per-hour-dense');
    await table.getByRole('row').last().scrollIntoViewIfNeeded();
    await capture(page, info, 'points-per-hour-dense-bottom');

    state = 'error';
    await page.reload();
    const error = page.getByRole('alert');
    await expect(error).toBeVisible();
    await capture(page, info, 'points-per-hour-error');
    state = 'loading';
    await error.getByRole('button', { name: 'Попробовать ещё раз' }).click();
    await expect(page.getByTestId('loading')).toBeVisible();
    await capture(page, info, 'points-per-hour-loading');
    state = 'empty';
    release?.();
    await expect(page.getByRole('heading', { name: 'В сезоне пока нет игроков' })).toBeVisible();
    await expect(table).toHaveCount(0);
    await expect(order).toHaveCount(0);
    await capture(page, info, 'points-per-hour-empty');
  } finally {
    release?.();
    await page.unrouteAll({ behavior: 'wait' });
    await world.adminApi.dispose();
  }
});
