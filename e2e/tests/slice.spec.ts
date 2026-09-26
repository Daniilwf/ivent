import { expect, test, type Browser, type Page } from '@playwright/test';

// Development seed accounts (src/GameEvent.Web/Hosting/DevSeed.cs); the password is the dev-only
// DevSeed:Password from appsettings.Development.json.
const password = 'dev-password';

async function signIn(browser: Browser, login: string): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto('/');
  await page.getByTestId('login-name').fill(login);
  await page.getByTestId('login-password').fill(password);
  await page.getByTestId('login-submit').click();
  await expect(page.getByTestId('turn')).toBeVisible();
  return page;
}

/** The id of the cell where the player's token stands. */
async function cellOf(page: Page, name: string): Promise<string | null> {
  return page
    .getByTestId('cells')
    .locator('[data-testid^="token-"]', { hasText: name })
    .locator('xpath=ancestor::li[1]')
    .getAttribute('data-testid');
}

// Each project plays with its own player, so desktop and phone can run at the same time.
const actors: Record<string, { login: string; name: string }> = {
  desktop: { login: 'vasya', name: 'Вася' },
  phone: { login: 'masha', name: 'Маша' },
};

test('slice: roll → start → complete moves the token, and another browser sees it without reloading', async ({
  browser,
}, testInfo) => {
  const actor = actors[testInfo.project.name] ?? { login: 'vasya', name: 'Вася' };

  // Petya watches the season in his own browser
  const watcher = await signIn(browser, 'petya');
  const before = await cellOf(watcher, actor.name);

  // The actor rolls, starts and completes a game
  const player = await signIn(browser, actor.login);
  await player.getByTestId('roll').click();
  await expect(player.getByTestId('offer')).toBeVisible();
  await player.getByTestId('start').click();
  await expect(player.getByTestId('active-run')).toBeVisible();
  // A game without pool hours needs an estimate with its source (D-96)
  const hours = player.getByTestId('complete-hours');
  if (await hours.isVisible()) {
    await hours.fill('5');
    await player.getByTestId('complete-hours-source').fill('HowLongToBeat');
  }
  // The difficulty is a row of radio pills (H4): the pill's label is what takes the tap
  await player.getByTestId('complete-difficulty').getByText('Нормальная', { exact: true }).click();
  await expect(
    player.getByTestId('complete-difficulty').getByRole('radio', { name: 'Нормальная' }),
  ).toBeChecked();
  await player.getByTestId('complete-submit').click();

  // The dice are shown to the actor, and the token has moved
  await expect(player.getByTestId('last-dice')).toBeVisible();
  await expect(player.getByTestId('roll')).toBeVisible();

  // Petya's page follows without a reload
  await expect.poll(() => cellOf(watcher, actor.name), { timeout: 10_000 }).not.toBe(before);
});
