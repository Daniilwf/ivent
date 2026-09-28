import { expect, test } from '@playwright/test';
import { Api } from '../../support/api.ts';
import { compressingProxy } from '../../support/compressing-proxy.ts';
import { speedPlayer } from '../../support/global-setup.ts';
import { baseURL, testPassword } from '../../support/world.ts';

// TESTING.md scenario 13 «Скорость главной на демо-сезоне»: the main page of the demo season (16 players, hundreds of
// events; seeded by scripts/e2e-server.mjs) over an emulated mobile network shows its main content — the season's name,
// the turn card and the map with the tokens — within the budget of docs/DESIGN.md «Производительность»: 2.5 s. A first
// visit: an empty cache, the session already signed in. The network is Lighthouse's «Slow 4G» (150 ms, 1.6 Mbit/s
// down, 750 kbit/s up, D-233). It runs after the parallel scenarios so the machine's load does not blur the time.

const budgetMs = 2_500;
const slow4g = {
  offline: false,
  latency: 150,
  downloadThroughput: (1.6 * 1024 * 1024) / 8,
  uploadThroughput: (750 * 1024) / 8,
};

test('speed: the demo season’s main page shows its content within 2.5 s on a mobile network', async ({
  browser,
}, testInfo) => {
  // The player the global setup added to the demo season, signed in through the API
  const player = await Api.signIn(baseURL(), speedPlayer.login, testPassword).catch(
    (error: unknown) => {
      throw new Error(
        `No demo player ${speedPlayer.login}: the global setup adds it on the E2E site (E2E_LOCAL_SITE=1 for a site given in E2E_BASE_URL, D-231).`,
        { cause: error },
      );
    },
  );
  const context = await browser.newContext();
  await context.addCookies(await player.cookies());
  await player.dispose();
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', slow4g);

  // Through gzip, as the live site answers behind Caddy
  const proxy = await compressingProxy(baseURL());
  await page.goto(proxy.url, { waitUntil: 'commit' });
  // The moment the main content is on the screen, by the page's own clock from the start of the navigation (checked on
  // every frame: the test's polling adds nothing to the number)
  const shown = await page.waitForFunction(
    () => {
      const ready =
        document.querySelector('h1')?.textContent === 'Демо-сезон' &&
        document.querySelector('[data-testid="turn"]') !== null &&
        document.querySelector('svg[role="application"] [data-player]') !== null;
      return ready ? performance.now() : null;
    },
    null,
    { polling: 'raf', timeout: 20_000 },
  );
  const elapsed = Math.round(Number(await shown.jsonValue()));
  await expect(page.getByTestId('turn')).toBeVisible();
  await context.close();
  await proxy.close();
  testInfo.annotations.push({ type: 'main content', description: `${String(elapsed)} ms` });
  expect(elapsed, `main content in ${String(elapsed)} ms`).toBeLessThanOrEqual(budgetMs);
});
