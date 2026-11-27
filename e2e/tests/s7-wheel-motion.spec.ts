import { expect, test } from '@playwright/test';
import { apiOf, seasonOf, setUpSeason, signIn } from '../support/world.ts';

test.use({
  reducedMotion: 'no-preference',
  contextOptions: { reducedMotion: 'no-preference' },
});

test('the server offer appears while the wheel renderer is still downloading', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Лиса'] });
  const [player] = world.players;
  if (!player) throw new Error('A player is required.');
  const owner = await apiOf(player);
  const page = await signIn(browser, player);
  let releaseMotion: (() => void) | undefined;
  const motion = new Promise<void>((resolve) => {
    releaseMotion = resolve;
  });
  let requestedMotion = false;
  try {
    await page.clock.install();
    await page.route('**/assets/motionFeatures-*.js', async (route) => {
      requestedMotion = true;
      await motion;
      await route.continue();
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    expect(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(
      false,
    );
    const roll = page.getByTestId('roll');
    await expect(roll).toBeVisible();
    // Exercise the exact animation deadline even when parallel builds delay the browser process.
    await page.clock.pauseAt(await page.evaluate(() => Date.now() + 500));
    await roll.click();
    const wheel = page.getByTestId('wheel');
    await expect(wheel.getByRole('button', { name: 'Показать результат' })).toBeVisible();
    await page.clock.runFor(2999);
    await expect(page.getByTestId('offer')).toBeVisible();
    expect(requestedMotion).toBe(true);

    const offer = (await seasonOf(owner, world.seasonId)).me?.offer;
    if (!offer) throw new Error('The server did not record an offer.');
    await expect(page.locator('#roll-result-title')).toHaveText(offer.title);
  } finally {
    releaseMotion?.();
    await page.unrouteAll({ behavior: 'wait' });
    await page.context().close();
    await owner.dispose();
    await world.adminApi.dispose();
  }
});
