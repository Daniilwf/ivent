import { expect, test } from '@playwright/test';
import { holdingTheClock } from '../support/clock.ts';
import {
  adminRefresh,
  advanceClock,
  apiOf,
  commandId,
  completeHere,
  confirmButton,
  goToAdminSection,
  openAdmin,
  rollAndStart,
  rollAndStartHere,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

// TESTING.md scenario 6 «Дедлайн» (SPEC «Прогресс и победа», «Сезон»): after the deadline the season is closing — no
// rolls, no completions, a game not completed does not count — but proofs are still accepted; the results come only
// after every proof is checked. The deadline is set by the scenario «дедлайн через час» (test endpoint) and the site's
// clock is moved past it.

test('deadline: rolls closed, proofs accepted, results only after every proof is checked', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [vasya, petya] = world.players;
  if (!vasya || !petya) throw new Error('No players.');

  // Before the deadline: Vasya completes a game (the proof comes later), Petya is still playing his
  const vasyaPage = await signIn(browser, vasya);
  await rollAndStartHere(vasyaPage);
  await completeHere(vasyaPage);
  const petyaApi = await apiOf(petya);
  await rollAndStart(petyaApi, world.seasonId);
  const petyaPage = await signIn(browser, petya);
  await expect(petyaPage.getByTestId('complete-form')).toBeVisible();

  // The deadline in an hour, then an hour and a minute later
  await holdingTheClock(async () => {
    await world.adminApi.post(`/api/test/seasons/${world.seasonId}/scenarios/deadline-in-hour`, {});
    await expect(vasyaPage.getByTestId('season-deadline')).toBeVisible();
    await advanceClock(world.adminApi, 61);
  });

  // The scheduler closes the season; the pages follow without a reload
  const closing = 'Дедлайн прошёл: броски закрыты, пруфы принимаются.';
  await expect(vasyaPage.getByTestId('season-status')).toContainText(closing, adminRefresh);
  await expect(vasyaPage.getByTestId('turn')).toContainText('Ждём проверку пруфов');
  await expect(vasyaPage.getByTestId('roll')).toHaveCount(0);
  // Petya's game was not completed in time: nothing to complete it with any more
  await expect(petyaPage.getByTestId('season-status')).toContainText(closing, adminRefresh);
  await expect(petyaPage.getByTestId('complete-form')).toHaveCount(0);
  await expect(petyaPage.getByTestId('roll')).toHaveCount(0);

  // A forged roll or completion is refused by the server
  const vasyaApi = await apiOf(vasya);
  for (const [who, action, body] of [
    [vasyaApi, 'roll', {}],
    [
      petyaApi,
      'complete',
      { difficulty: 'normal', estimatedHours: 5, hoursSource: 'HowLongToBeat' },
    ],
  ] as const) {
    const answer = await who.send('POST', `/api/seasons/${world.seasonId}/${action}`, {
      commandId: commandId(),
      ...body,
    });
    expect(answer.status(), `${action} after the deadline`).toBe(409);
  }

  // The proof is still accepted
  await vasyaPage.getByTestId('proof-details').locator('summary').click();
  await vasyaPage.getByTestId('proof-link').fill('https://example.com/credits.png');
  await vasyaPage.getByTestId('proof-submit').click();
  await expect(vasyaPage.getByTestId('proof-status')).toContainText('Пруф ждёт проверки админом.');

  // The admin cannot sum up while a proof waits...
  const admin = await signIn(browser, world.admin);
  await openAdmin(admin, world, 'season');
  await expect(admin.getByTestId('season-status')).toContainText('Ждёт проверки пруфов');
  await admin.getByTestId('season-next').click();
  await confirmButton(admin, 'Подвести итоги').click();
  await expect(admin.getByTestId('admin-season')).toContainText(
    'Сначала проверь все пруфы: итоги — только после проверки.',
  );

  // ...approves it, and then can
  await goToAdminSection(admin, 'proofs');
  await admin.getByTestId('proof-queue').getByTestId('approve').click();
  await expect(admin.getByTestId('proof-queue')).toContainText('Одобрено');
  await goToAdminSection(admin, 'season');
  await admin.getByTestId('season-next').click();
  await confirmButton(admin, 'Подвести итоги').click();
  await expect(admin.getByTestId('season-status')).toContainText('Завершён');

  // The players see the results; Vasya's completed game counts, Petya's does not
  await expect(vasyaPage.getByTestId('turn')).toContainText('Сезон завершён');
  const final = await seasonOf(vasyaApi, world.seasonId);
  expect(final.status).toBe('finished');
  const points = (id: string) => final.players.find((p) => p.id === id)?.points;
  expect(points(vasya.playerId)).toBeGreaterThan(0);
  expect(points(petya.playerId)).toBe(0);
  expect(final.leaderboard.map((r) => r.playerId)).toEqual([vasya.playerId, petya.playerId]);
});
