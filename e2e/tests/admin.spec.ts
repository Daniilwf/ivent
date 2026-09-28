import { expect, test } from '@playwright/test';
import { seedAdmin } from '../support/api.ts';
import { signInHere } from '../support/world.ts';

// H8: the admin's pages end to end on the development seed — the admin opens them from the menu, the proof queue
// comes first, the sections switch (a side column on a desktop, a sheet on a phone) and the address follows; a player
// at the same address sees the game. Read-only: the other tests play on the same seed.
const password = seedAdmin.password;

test('the admin opens the admin pages from the menu and walks the sections', async ({
  page,
}, testInfo) => {
  await page.goto('/');
  await signInHere(page, { login: 'admin', password });

  await page.getByTestId('user-menu').click();
  await page.getByTestId('to-admin').click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Пруфы');

  if (testInfo.project.name === 'phone') await page.getByTestId('admin-sections').click();
  await page.getByTestId('admin-nav-log').click();
  await expect(page).toHaveURL(/\/admin\/log$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Лог');
  await expect(page.getByTestId('admin-log').getByRole('article').first()).toBeVisible();

  // No horizontal scroll on either size
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
});

test('a player at the admin address sees the game', async ({ page }) => {
  await page.goto('/');
  await signInHere(page, { login: 'dasha', password });
  await expect(page.getByTestId('turn')).toBeVisible();

  await page.goto('/admin/players');

  await expect(page.getByTestId('turn')).toBeVisible();
  await expect(page.getByTestId('admin')).toHaveCount(0);
});
