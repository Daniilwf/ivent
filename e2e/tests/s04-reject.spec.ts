import { expect, test } from '@playwright/test';
import {
  adminRefresh,
  apiOf,
  cellOf,
  complete,
  confirmButton,
  leaderRow,
  openAdmin,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

// TESTING.md scenario 4 «Реджект»: the admin rejects a completed run in the proof queue; the points and cells it gave
// are taken back (SPEC «Награда за прохождение»), and the player's page says so without a reload.

test('reject: the points and cells of the run are taken back', async ({ browser }) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');

  // Setup: Vasya completed a game and sent a proof
  const player = await apiOf(vasya);
  const game = await rollAndStart(player, world.seasonId);
  await complete(player, world.seasonId);
  let view = await seasonOf(player, world.seasonId);
  const run = view.me?.lastCompleted;
  if (!run) throw new Error('No completed run.');
  await player.post(`/api/seasons/${world.seasonId}/runs/${run.id}/proof`, {
    links: ['https://example.com/not-really-the-credits.png'],
  });
  const me = () => view.players.find((p) => p.id === vasya.playerId);
  expect(me()?.points).toBe(run.total);

  // Vasya's page shows the dice and the token away from the start
  const page = await signIn(browser, vasya);
  await expect(page.getByTestId('last-dice')).toContainText(`итого ${String(run.total)}`);
  const startCell = `cell-${view.cells[0]?.id ?? ''}`;
  expect(await cellOf(page, 'Вася')).not.toBe(startCell);

  // The admin rejects with a comment for the log
  const admin = await signIn(browser, world.admin);
  await openAdmin(admin, world, 'proofs');
  const card = admin.getByTestId(`proof-${run.id}`);
  await expect(card).toContainText(game, adminRefresh);
  await card.getByTestId('reject').click();
  await expect(admin.getByRole('alertdialog')).toContainText(String(run.total));
  await admin.getByTestId('reject-comment').fill('На скрине не титры');
  await confirmButton(admin, 'Отклонить прохождение').click();
  await expect(admin.getByTestId('proof-queue')).toContainText(`Отклонено: ${game}`);

  // Vasya's page follows: the run is rejected, the points are 0, the token is back on the start
  await expect(page.getByTestId('last-dice')).toHaveText(
    `Прохождение отклонено (${game}): очки и клетки сняты.`,
  );
  await expect.poll(() => cellOf(page, 'Вася')).toBe(startCell);
  await expect(leaderRow(page, vasya.playerId)).toContainText('Вася: 0 очк.');
  await expect(page.getByTestId('proof-status')).toContainText('Пруф отклонён.');
  view = await seasonOf(player, world.seasonId);
  expect(me()?.points).toBe(0);
  expect(me()?.cellId).toBe(view.cells[0]?.id);
  expect(view.me?.lastCompleted?.status).toBe('rejected');
});
