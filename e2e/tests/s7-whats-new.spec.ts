import { expect, test } from '@playwright/test';
import { setUpSeason, signIn } from '../support/world.ts';

test('opens the release journal from the menu and its own address', async ({ browser }) => {
  const world = await setUpSeason({ players: ['Лиса'] });
  const [player] = world.players;
  if (!player) throw new Error('A player is required.');
  const page = await signIn(browser, player);
  const season = page.url();
  const menu = page.getByTestId('user-menu');
  const heading = page.getByRole('heading', { level: 1, name: 'Что нового' });

  await expect(page.getByTestId('update-banner')).toHaveCount(0);
  await menu.press('Enter');
  await page.getByRole('menuitem', { name: 'Что нового' }).press('Enter');
  await expect(page).toHaveURL(/\/whats-new$/);
  await expect(heading).toBeFocused();
  await expect
    .poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth))
    .toBe(true);

  await page.goBack();
  await expect(page).toHaveURL(season);
  await expect(menu).toBeVisible();

  await page.goto('/whats-new');
  await expect(heading).toBeVisible();
  // Make the page scrollable even before the first real release has been recorded.
  const viewport = page.viewportSize();
  if (!viewport) throw new Error('A viewport is required.');
  await page.setViewportSize({ ...viewport, height: 160 });
  const position = await page.evaluate(() => {
    window.scrollTo(0, document.documentElement.scrollHeight);
    return window.scrollY;
  });
  expect(position).toBeGreaterThan(0);
  await page.reload();
  await expect(heading).toBeVisible();
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(position);
});
