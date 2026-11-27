import { expect, test } from '@playwright/test';
import { seedAdmin } from '../support/api.ts';
import { demoSeasonId } from '../support/global-setup.ts';
import { signIn } from '../support/world.ts';

test('the long phone leaderboard keeps its title and close button in view while ranks scroll', async ({
  browser,
}, info) => {
  test.skip(info.project.name !== 'phone', 'The desktop uses an inline leaderboard.');
  const page = await signIn(browser, { login: 'player02', password: seedAdmin.password });
  await page.goto(`/seasons/${demoSeasonId}`);

  const opener = page.getByRole('button', { name: /Лидерборд/ });
  await opener.tap();
  const sheet = page.getByRole('dialog', { name: 'Лидерборд' });
  const heading = sheet.getByRole('heading', { name: 'Лидерборд' });
  const close = sheet.getByRole('button', { name: 'Закрыть' });
  await expect(sheet.locator('[data-me]')).toBeInViewport();
  await expect(heading).toBeInViewport();
  await expect(close).toBeInViewport();

  const last = sheet.getByRole('listitem').last();
  await last.evaluate((row) => {
    row.scrollIntoView({ block: 'end' });
  });
  await expect(last).toBeInViewport();
  await expect(heading).toBeInViewport();
  await expect(close).toBeInViewport();
  await page.screenshot({ path: info.outputPath('leaderboard-scrolled.png'), scale: 'css' });
  await close.tap();
  await expect(sheet).toHaveCount(0);
  await expect(opener).toBeFocused();

  const mapOpener = page.getByTestId('map-open');
  await mapOpener.tap();
  const mapSheet = page.getByRole('dialog');
  await expect(mapSheet.getByRole('application')).toBeInViewport();
  expect(await mapSheet.evaluate((dialog) => dialog.clientHeight)).toBe(
    await page.evaluate(() => window.innerHeight),
  );
  await page.keyboard.press('Escape');
  await expect(mapSheet).toHaveCount(0);
  await expect(mapOpener).toBeFocused();
});
