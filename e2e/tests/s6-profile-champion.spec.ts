import { expect, test } from '@playwright/test';
import { PNG } from 'pngjs';
import { setUpSeason, signIn } from '../support/world.ts';

test('a spectator sees the title on both saved first-place profiles', async ({ browser }, info) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'], spectator: true });
  const [first, tied] = world.players;
  if (!first || !tied || !world.spectator) throw new Error('Incomplete season fixture.');

  const page = await signIn(browser, world.spectator);
  const seasonRow = page.getByTestId('profile-seasons').getByRole('listitem');
  const title = seasonRow.getByText('Чемпион сезона', { exact: true });
  const place = seasonRow.getByText('1 место', { exact: true });

  await page.goto(`/users/${first.id}`);
  await expect(seasonRow.getByRole('link', { name: world.seasonName, exact: true })).toBeVisible();
  await expect(title).toHaveCount(0);

  const statusPath = `/api/admin/seasons/${world.seasonId}/status`;
  await world.adminApi.post(statusPath, { to: 'closing' });
  await world.adminApi.post(statusPath, { to: 'finished' });
  await page.reload();
  await expect(seasonRow.getByText('Завершён', { exact: true })).toBeVisible();
  await expect(title).toBeVisible();
  await expect(place).toBeVisible();

  await world.adminApi.post(statusPath, { to: 'archived' });
  await page.reload();
  await expect(seasonRow.getByText('В архиве', { exact: true })).toBeVisible();
  await expect(title).toBeVisible();
  await expect(place).toBeVisible();
  await expect(seasonRow.getByRole('link', { name: world.seasonName, exact: true })).toBeVisible();

  const viewport =
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
  expect(page.viewportSize()).toEqual(viewport);
  await page.evaluate(() => document.fonts.ready);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    viewport.width,
  );
  const screenshot = info.outputPath(`profile-champion-${info.project.name}.png`);
  const png = PNG.sync.read(await page.screenshot({ path: screenshot, scale: 'css' }));
  expect({ width: png.width, height: png.height }).toEqual(viewport);
  await info.attach('archived champion profile', { path: screenshot, contentType: 'image/png' });

  await page.goto(`/users/${tied.id}`);
  await expect(page.getByRole('heading', { level: 1, name: tied.name, exact: true })).toBeVisible();
  await expect(title).toBeVisible();
  await expect(place).toBeVisible();
});
