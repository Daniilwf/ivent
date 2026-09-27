import { expect, test } from '@playwright/test';
import { holdingTheClock } from '../support/clock.ts';
import {
  advanceClock,
  apiOf,
  commandId,
  openAdmin,
  rollAndStartHere,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

// TESTING.md scenario 3 «Тех-реролл в окне и после окна»: within 48 hours of the roll the player's own tech reroll is
// free and brings a new roll; 49 hours later the button is gone, the server refuses a forged request, and only the
// admin can do it (SPEC «Реролл, дроп, тех-реролл»). The site's clock is moved forward through the test endpoint.

test('tech reroll: free within the window, closed 49 hours after the roll, then only through the admin', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');
  const page = await signIn(browser, vasya);
  const player = await apiOf(vasya);

  // The window and the jump over it hold the site's clock: another test moving it would close the window early
  await holdingTheClock(async () => {
    // Within the window: a reason, and a new roll at once, with no penalty
    const first = await rollAndStartHere(page);
    await page.getByTestId('tech-reroll').click();
    await expect(page.getByTestId('tech-reroll-until')).toBeVisible();
    await page.getByTestId('tech-reroll-reason').selectOption('doesNotLaunch');
    await page.getByTestId('tech-reroll-submit').click();
    await expect(page.getByTestId('offer')).toBeVisible();
    const second = (await page.locator('#roll-result-title').textContent())?.trim();
    // The tech-rerolled game is excluded for the player
    expect(second).not.toBe(first);
    let view = await seasonOf(player, world.seasonId);
    expect(view.players.find((p) => p.id === vasya.playerId)?.points).toBe(0);
    expect(view.cells.findIndex((c) => c.id === view.players[0]?.cellId)).toBe(0);

    // The new game is started; 49 hours pass
    await page.getByTestId('start').click();
    await expect(page.getByTestId('active-run')).toBeVisible();
    await expect(page.getByTestId('tech-reroll')).toBeVisible();
    await advanceClock(world.adminApi, 49 * 60);

    // The window has closed: the page says so and offers no button...
    await page.reload();
    await expect(page.getByTestId('active-run')).toBeVisible();
    await expect(page.getByTestId('tech-reroll-closed')).toBeVisible();
    await expect(page.getByTestId('tech-reroll')).toHaveCount(0);

    // ...and a forged request is refused
    const forged = await player.send('POST', `/api/seasons/${world.seasonId}/tech-reroll`, {
      commandId: commandId(),
      reason: 'doesNotLaunch',
    });
    expect(forged.status()).toBe(409);
    view = await seasonOf(player, world.seasonId);
    expect(view.me?.phase).toBe('playing');
  });

  // The admin still can: from the player's card, with a reason and a comment
  const admin = await signIn(browser, world.admin);
  await openAdmin(admin, world, 'players');
  const card = admin.getByTestId(`player-${vasya.playerId}`);
  await card.getByTestId('tech-reroll-open').click();
  await card.getByTestId('tech-reroll-reason').selectOption('paidUnavailable');
  await card.getByTestId('tech-reroll-comment').fill('Игру убрали из магазина');
  await card.getByTestId('tech-reroll-submit').click();
  await expect(admin.getByTestId('admin-players')).toContainText('Вася');

  // The player's page follows: a new roll to answer
  await expect(page.getByTestId('offer')).toBeVisible();
  const view = await seasonOf(player, world.seasonId);
  expect(view.me?.phase).toBe('rolling');
});
