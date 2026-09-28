import { expect, test } from '@playwright/test';
import {
  adminRefresh,
  apiOf,
  complete,
  completeHere,
  confirmButton,
  leaderRow,
  openAdmin,
  rollAndStart,
  rollAndStartHere,
  seasonOf,
  setUpSeason,
  signIn,
  type World,
} from '../support/world.ts';

// TESTING.md scenario 5 «Финиш» (SPEC «Прогресс и победа», «Первое место»): Vasya finishes first — provisionally, until
// his proof is approved; Petya finishes second and gets the bonus of the second place; the admin rejects Vasya's
// finishing run — Vasya leaves the finish, Petya becomes the first, his bonus goes; the admin approves Petya's run —
// his first place is final and he is frozen: a later completion gives him no points. The scenario «финиш на носу»
// (test endpoint) puts each of them one cell before the finish.

async function finishSoon(world: World, login: string) {
  await world.adminApi.post(`/api/test/seasons/${world.seasonId}/scenarios/finish-soon`, {
    player: login,
  });
}

test('finish: provisional first, a second with the bonus, the first rejected, the new first approved and frozen', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [vasya, petya] = world.players;
  if (!vasya || !petya) throw new Error('No players.');
  const secondBonus = 10; // finish.bonusByOrder[0] of docs/ruleset.default.json: the second finisher's bonus

  // Vasya finishes first from one cell before the finish: first, provisionally
  await finishSoon(world, vasya.login);
  const vasyaPage = await signIn(browser, vasya);
  await rollAndStartHere(vasyaPage);
  await completeHere(vasyaPage);
  // The dice's result, said in words once the token stands
  await expect(vasyaPage.getByTestId('throw-result')).toContainText('Фишка на финише — ты первый!');
  await expect(leaderRow(vasyaPage, vasya.playerId)).toContainText(
    'на финише — первое место (предварительно)',
  );
  const vasyaApi = await apiOf(vasya);
  let view = await seasonOf(vasyaApi, world.seasonId);
  const vasyaRun = view.me?.lastCompleted;
  if (!vasyaRun) throw new Error('No finishing run.');
  expect(view.me?.finish).toEqual({ order: 1, frozen: false });

  // Petya finishes second: the bonus of the second place on top of his dice
  await finishSoon(world, petya.login);
  const petyaPage = await signIn(browser, petya);
  await rollAndStartHere(petyaPage);
  await completeHere(petyaPage);
  const petyaApi = await apiOf(petya);
  view = await seasonOf(petyaApi, world.seasonId);
  const petyaRun = view.me?.lastCompleted;
  if (!petyaRun) throw new Error('No finishing run.');
  expect(view.me?.finish?.order).toBe(2);
  const petyaPoints = petyaRun.total + secondBonus;
  await expect(leaderRow(petyaPage, petya.playerId)).toContainText(
    `Петя: ${String(petyaPoints)} очк., на финише`,
  );
  // The first stays on top whatever the points (Vasya may have fewer)
  await expect(leaderRow(petyaPage, vasya.playerId)).toContainText('1. Вася');

  // The admin: the finishes come first in the queue; Vasya's run is rejected
  const admin = await signIn(browser, world.admin);
  await openAdmin(admin, world, 'proofs');
  const vasyaCard = admin.getByTestId(`proof-${vasyaRun.id}`);
  await expect(vasyaCard).toContainText('Решает финиш', adminRefresh);
  await vasyaCard.getByTestId('reject').click();
  await admin.getByTestId('reject-comment').fill('Пруфа нет');
  await confirmButton(admin, 'Отклонить прохождение').click();
  await expect(admin.getByTestId('proof-queue')).toContainText('Отклонено');

  // The places are recalculated: Petya is the first (provisionally), his bonus is gone; Vasya is off the finish
  await expect(leaderRow(petyaPage, petya.playerId)).toContainText(
    `1. Петя: ${String(petyaRun.total)} очк., на финише — первое место (предварительно)`,
  );
  await expect(leaderRow(vasyaPage, vasya.playerId)).toContainText('2. Вася: 0 очк., до финиша');
  view = await seasonOf(vasyaApi, world.seasonId);
  expect(view.me?.finish).toBeNull();

  // The admin approves Petya's run: the first place is final, Petya is frozen
  const petyaCard = admin.getByTestId(`proof-${petyaRun.id}`);
  await petyaCard.getByTestId('approve-comment').fill('Видел сам');
  await petyaCard.getByTestId('approve').click();
  await expect(admin.getByTestId('proof-queue')).toContainText('Одобрено');
  await expect(leaderRow(petyaPage, petya.playerId)).toContainText(
    `1. Петя: ${String(petyaRun.total)} очк., на финише — первое место`,
  );
  await expect(leaderRow(petyaPage, petya.playerId)).not.toContainText('предварительно');
  view = await seasonOf(petyaApi, world.seasonId);
  // The order of finishing is never reused (D-99): he finished second, he holds the first place now
  expect(view.me?.finish).toEqual({ order: 2, frozen: true });

  // Frozen: another game completed in free mode gives no points and does not move the token
  await rollAndStart(petyaApi, world.seasonId);
  await complete(petyaApi, world.seasonId);
  const after = await seasonOf(petyaApi, world.seasonId);
  const me = after.players.find((p) => p.id === petya.playerId);
  expect(me?.points).toBe(petyaRun.total);
  expect(after.leaderboard[0]?.playerId).toBe(petya.playerId);
  await expect(leaderRow(petyaPage, petya.playerId)).toContainText(
    `1. Петя: ${String(petyaRun.total)} очк., на финише`,
  );
});
