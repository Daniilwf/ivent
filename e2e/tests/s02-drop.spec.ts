import { expect, test } from '@playwright/test';
import { holdingTheClock } from '../support/clock.ts';
import {
  adminRefresh,
  apiOf,
  leaderRow,
  openAdmin,
  rollAndStartHere,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

// TESTING.md scenario 2 «Дроп»: the penalty dice take points and cells alike (SPEC «Реролл, дроп, тех-реролл»), and the
// mandatory bad event waits in the list of manual effects — the player's and the admin's.

test('drop: the penalty takes points and cells, and a manual bad event joins the list', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');

  // Setup: Vasya stands on cell 11 with 20 points, far from the start, so the whole penalty shows
  const start = await seasonOf(world.adminApi, world.seasonId);
  const cell = start.cells[10];
  if (!cell) throw new Error('The map is too short.');
  await world.adminApi.post(
    `/api/admin/seasons/${world.seasonId}/players/${vasya.playerId}/adjust`,
    { comment: 'Сценарий: подальше от старта', cellId: cell.id, pointsDelta: 20 },
  );

  const page = await signIn(browser, vasya);
  // The hour of play before the hint goes is counted by the site's clock: held, so no other test moves it meanwhile
  await holdingTheClock(async () => {
    const game = await rollAndStartHere(page);

    // The drop is confirmed in a window that says what it costs
    await page.getByTestId('drop').click();
    const confirm = page.getByTestId('drop-confirm');
    await expect(confirm).toContainText(`Дропнуть «${game}»?`);
    // A drop right after the start: the hint to wait an hour, only a hint (RR4)
    await expect(confirm.getByTestId('drop-hint')).toBeVisible();
    await page.getByTestId('drop-confirm-yes').click();
    await expect(page.getByTestId('roll')).toBeVisible();
  });

  // Back to the roll, with the bad event to play by hand
  await expect(page.getByTestId('roll')).toBeVisible();
  const effects = page.getByTestId('manual-effects');
  await expect(effects).toContainText('Плохой ивент (за дроп)');

  // The same number of points and cells went: 2d4 of the default rules
  const player = await apiOf(vasya);
  const after = await seasonOf(player, world.seasonId);
  const me = after.players.find((p) => p.id === vasya.playerId);
  if (!me) throw new Error('Vasya is not on the map.');
  const lostPoints = 20 - me.points;
  const lostCells = 10 - after.cells.findIndex((c) => c.id === me.cellId);
  expect(lostPoints).toBeGreaterThanOrEqual(2);
  expect(lostPoints).toBeLessThanOrEqual(8);
  expect(lostCells).toBe(lostPoints);
  expect(after.me?.manualEffects.map((e) => [e.drawEvent, e.source])).toEqual([['bad', 'drop']]);

  // The screen shows the same: the leaderboard's row and the token's cell
  const row = after.leaderboard.find((r) => r.playerId === vasya.playerId);
  await expect(leaderRow(page, vasya.playerId)).toHaveText(
    `1. Вася: ${String(me.points)} очк., до финиша ${String(row?.cellsToFinish)} кл.`,
  );
  await expect(page.getByTestId(`cell-${me.cellId}`)).toContainText('Вася');

  // The admin sees the bad event waiting in «Ручные эффекты»
  const admin = await signIn(browser, world.admin);
  await openAdmin(admin, world, 'effects');
  await expect(admin.getByTestId('admin-effects')).toContainText('Вася', adminRefresh);
  await expect(admin.getByTestId('admin-effects')).toContainText('Плохой ивент');
});
