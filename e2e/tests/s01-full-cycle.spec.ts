import { expect, test, type Page } from '@playwright/test';
import {
  cellOf,
  completeHere,
  createAccount,
  goToAdminSection,
  rollAndStartHere,
  signIn,
  signInHere,
  testPassword,
  uniqueTag,
  adminRefresh,
} from '../support/world.ts';
import { Api, seedAdmin } from '../support/api.ts';
import { baseURL } from '../support/world.ts';

// TESTING.md scenario 1 «Полный цикл»: the admin creates a season and three players in the admin pages → the players
// sign in with the temporary password → a roll → a run with a proof → dice → the token moves → another player sees it
// without a reload → the admin approves. Everything the scenario is about goes through the screens; only the admin's
// own account is made through the API.

async function createPlayerHere(page: Page, login: string, name: string): Promise<string> {
  await page.getByTestId('account-login').fill(login);
  await page.getByTestId('account-name').fill(name);
  await page.getByTestId('account-role').selectOption('player');
  await page.getByTestId('account-create').click();
  const secret = page.getByTestId('temporary-password');
  await expect(secret).toContainText(login);
  const password = (await secret.locator('p').nth(1).textContent())?.trim() ?? '';
  expect(password).toMatch(/^[a-z0-9]{12}$/);
  return password;
}

test('full cycle: the admin sets up a season, a player completes a game, others see it, the admin approves', async ({
  browser,
}) => {
  const tag = uniqueTag();
  const seed = await Api.signIn(baseURL(), seedAdmin.login, seedAdmin.password);
  const adminAccount = await createAccount(seed, 'admin', `a-${tag}`, `Админ ${tag}`);
  await seed.dispose();

  // The admin: three accounts, the season, the players in it, the start
  const admin = await signIn(browser, adminAccount);
  await admin.goto('/admin/accounts');
  const names = ['Вася', 'Петя', 'Маша'];
  const logins = names.map((_, i) => `p${String(i + 1)}-${tag}`);
  const passwords: string[] = [];
  for (const [i, name] of names.entries())
    passwords.push(await createPlayerHere(admin, logins[i] ?? '', name));

  await goToAdminSection(admin, 'season');
  const seasonName = `Сезон ${tag}`;
  await admin.getByTestId('season-name').fill(seasonName);
  await admin.getByTestId('season-create').click();
  await expect(admin.getByTestId('season-status')).toContainText(seasonName);

  await goToAdminSection(admin, 'players');
  for (const [i, name] of names.entries()) {
    await admin.getByTestId('add-player-open').click();
    await admin
      .getByTestId('add-player-account')
      .selectOption({ label: `${name} (${logins[i] ?? ''})` });
    await admin.getByTestId('add-player-submit').click();
    await expect(admin.getByTestId('admin-players')).toContainText(name);
  }

  await goToAdminSection(admin, 'season');
  await admin.getByTestId('season-next').click();
  await admin.getByRole('alertdialog').getByRole('button', { name: 'Начать сезон' }).click();
  await expect(admin.getByTestId('season-status')).toContainText('Идёт');

  // The players come in with the temporary password and set their own
  async function firstVisit(i: number): Promise<Page> {
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.goto('/');
    await page.getByTestId('login-name').fill(logins[i] ?? '');
    await page.getByTestId('login-password').fill(passwords[i] ?? '');
    await page.getByTestId('login-submit').click();
    await page.getByTestId('password-current').fill(passwords[i] ?? '');
    await page.getByTestId('password-new').fill(testPassword);
    await page.getByTestId('password-repeat').fill(testPassword);
    await page.getByTestId('password-submit').click();
    await expect(page.getByTestId('turn')).toBeVisible();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(seasonName);
    return page;
  }
  const vasya = await firstVisit(0);
  const petya = await firstVisit(1);
  // The third signs in again with the new password: it works, the temporary one is gone
  const masha = await firstVisit(2);
  await masha.context().clearCookies();
  await masha.goto('/');
  await signInHere(masha, { login: logins[2] ?? '', password: testPassword });
  await expect(masha.getByTestId('turn')).toBeVisible();

  // Petya watches Vasya's token
  const before = await cellOf(petya, 'Вася');
  expect(before).not.toBeNull();

  // Vasya rolls, plays, completes: the dice, the token moves
  await rollAndStartHere(vasya);
  await completeHere(vasya);
  await expect(vasya.getByTestId('last-dice')).toContainText('Кубы за прохождение');
  await expect.poll(() => cellOf(vasya, 'Вася')).not.toBe(before);

  // ...and sends the proof: a link
  await vasya.getByTestId('proof-details').locator('summary').click();
  await vasya.getByTestId('proof-link').fill('https://example.com/credits.png');
  await vasya.getByTestId('proof-submit').click();
  await expect(vasya.getByTestId('proof-status')).toContainText('Пруф ждёт проверки админом.');

  // Petya's page follows without a reload
  await expect.poll(() => cellOf(petya, 'Вася'), { timeout: 10_000 }).not.toBe(before);
  await expect(petya.getByTestId('cells')).toContainText('Вася');

  // The admin approves the proof in the queue
  await goToAdminSection(admin, 'proofs');
  const card = admin.getByTestId('proof-queue').getByRole('article').first();
  await expect(card).toContainText('Вася', adminRefresh);
  await expect(card).toContainText('https://example.com/credits.png');
  await card.getByTestId('approve').click();
  await expect(admin.getByTestId('proof-queue')).toContainText('Одобрено');

  // Vasya sees the approval without a reload
  await expect(vasya.getByTestId('proof-status')).toContainText('Пруф одобрен.');
});
