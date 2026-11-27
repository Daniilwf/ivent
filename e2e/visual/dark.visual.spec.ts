import { expect, test, type Page } from '@playwright/test';

// The dark theme (D-940), in every engine and size of playwright.visual.config.ts: the key screens of the styleguide
// on a system set to dark with «Как в системе» (the default), so the media query path is the one checked. The light
// baselines stay in map.visual.spec.ts.

test.use({ colorScheme: 'dark' });

async function openStyleguide(page: Page, anchor: string) {
  await page.goto(`/styleguide#${anchor}`);
  await page.locator(`#${anchor}`).waitFor();
  await page.evaluate(() => document.fonts.ready);
}

test('the dark map looks as approved', async ({ page }) => {
  await openStyleguide(page, 'map');
  const map = page.locator('#map svg[role="application"]').first();
  await map.scrollIntoViewIfNeeded();
  await expect(map).toHaveScreenshot('dark-map.png');
});

for (const section of [
  'buttons',
  'fields',
  'states',
  'run',
  'inventory',
  'feed',
  'profile',
  'season-recap',
  'admin',
  'editor',
  'map-background',
  'drawn-event',
  'global-event',
  'poll-card',
]) {
  test(`the dark styleguide section «${section}» looks as approved`, async ({ page }) => {
    if (section === 'global-event' || section === 'poll-card')
      await page.clock.setFixedTime(new Date('2026-11-18T12:00:00Z'));
    await openStyleguide(page, section);
    if (section === 'map-background')
      await expect(page.locator('#map-background image[data-map-background]')).toHaveClass(
        /opacity-100/,
      );
    await expect(page.locator(`#${section}`)).toHaveScreenshot(`dark-${section}.png`);
  });
}
