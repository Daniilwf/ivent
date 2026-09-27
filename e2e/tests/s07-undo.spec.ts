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
  type World,
} from '../support/world.ts';
import type { Schemas } from '../support/api.ts';

// TESTING.md scenario 7 «Откат действия админом»: the admin undoes a whole command from the log (SPEC «Лог действий с
// откатом», invariant 12): a command later ones depend on is refused with the list of them; the latest is undone, and
// the season is back where it was before it.

async function commandOf(world: World, type: string): Promise<Schemas['AdminCommandView']> {
  const commands = await world.adminApi.get<Schemas['AdminCommandView'][]>(
    `/api/admin/seasons/${world.seasonId}/commands?limit=50`,
  );
  const found = commands.find((c) => c.commandType === type && !c.undone);
  if (!found) throw new Error(`No ${type} in the log.`);
  return found;
}

test('undo: a command with later ones is refused with them listed, the latest is undone whole', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');

  // Setup: Vasya rolled, started and completed a game
  const player = await apiOf(vasya);
  const game = await rollAndStart(player, world.seasonId);
  const before = await seasonOf(player, world.seasonId);
  await complete(player, world.seasonId);
  const done = await seasonOf(player, world.seasonId);
  expect(done.players[0]?.points).toBeGreaterThan(0);

  const page = await signIn(browser, vasya);
  await expect(page.getByTestId('last-dice')).toBeVisible();
  const admin = await signIn(browser, world.admin);
  await openAdmin(admin, world, 'log');

  // The roll has later commands: the start and the completion. The undo is refused and names them
  const roll = await commandOf(world, 'RollGame');
  await admin.getByTestId(`command-${roll.commandId}`).getByTestId('undo').click();
  await admin.getByTestId('undo-comment').fill('Проверка отката');
  await confirmButton(admin, 'Откатить действие').click();
  const dependents = admin.getByTestId('undo-dependents');
  await expect(dependents.getByRole('listitem')).toHaveCount(2);
  await admin.keyboard.press('Escape');
  await expect(admin.getByRole('alertdialog')).toHaveCount(0);

  // The completion is the latest: undone with its dice, points and move
  const completion = await commandOf(world, 'CompleteRun');
  const row = admin.getByTestId(`command-${completion.commandId}`);
  await row.getByTestId('undo').click();
  await admin.getByTestId('undo-comment').fill('Ошибочно отмечено пройденным');
  await confirmButton(admin, 'Откатить действие').click();
  await expect(row).toContainText('Откатано', adminRefresh);
  await expect(row.getByTestId('undo')).toHaveCount(0);

  // Vasya's page is back where it was before the completion: the game in progress, no points, the start
  await expect(page.getByTestId('active-run')).toBeVisible();
  await expect(page.getByTestId('active-run')).toContainText(game);
  await expect(leaderRow(page, vasya.playerId)).toContainText('Вася: 0 очк.');
  await expect.poll(() => cellOf(page, 'Вася')).toBe(`cell-${before.players[0]?.cellId ?? ''}`);
  const after = await seasonOf(player, world.seasonId);
  expect(after.players).toEqual(before.players);
  expect(after.leaderboard).toEqual(before.leaderboard);
  expect(after.me?.phase).toBe('playing');
});
