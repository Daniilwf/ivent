import { expect, test, type Page } from '@playwright/test';
import { goToAdminSection, openAdmin, setUpSeason, signIn } from '../support/world.ts';

// Security audit 2026-11 (SEC-1, D-462): the site's pages run under a policy that lets only the site's own scripts run.
// A policy too strict for the site would break it quietly — a style element of a dialog, the hub's WebSocket, the
// bug report's screenshot. So the main pages of a player and an admin are walked and the browser must report no
// violation of the policy on any of them.

/** Collects every violation of the page's policy, from the first script on */
async function watchPolicy(page: Page): Promise<string[]> {
  const violations: string[] = [];
  await page.exposeFunction('reportCspViolation', (text: string) => violations.push(text));
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) => {
      const report = (globalThis as unknown as { reportCspViolation: (t: string) => void })
        .reportCspViolation;
      report(`${e.violatedDirective} ${e.blockedURI} at ${location.pathname}`);
    });
  });
  page.on('console', (message) => {
    if (message.text().includes('Content Security Policy')) violations.push(message.text());
  });
  return violations;
}

test('csp: a player and an admin walk the site and nothing breaks the policy', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');

  const player = await signIn(browser, vasya);
  const playerViolations = await watchPolicy(player);
  await player.reload();
  await expect(player.getByTestId('turn')).toBeVisible();
  for (const address of ['/pool', '/feed', '/rules', `/users/${vasya.id}`]) {
    await player.goto(address);
    await expect(player.getByTestId('user-menu')).toBeVisible();
  }

  // The bug report draws the page into a picture (html-to-image: data: and blob: pictures, inline fonts)
  await player.goto('/');
  await player.getByTestId('bug-report').click();
  await expect(player.getByTestId('bug-report-dialog')).toBeVisible();
  await expect(player.getByTestId('bug-report-attach')).toBeVisible();
  await player.keyboard.press('Escape');

  const admin = await signIn(browser, world.admin);
  const adminViolations = await watchPolicy(admin);
  await openAdmin(admin, world, 'season');
  for (const section of ['players', 'log', 'rules', 'map', 'pool', 'accounts', 'bugs', 'errors']) {
    await goToAdminSection(admin, section);
  }

  expect(playerViolations).toEqual([]);
  expect(adminViolations).toEqual([]);
});
