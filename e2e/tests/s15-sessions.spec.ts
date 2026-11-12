import { expect, test } from '@playwright/test';
import { setUpSeason, signIn } from '../support/world.ts';

// D-464 (1), D-466: one player in two browsers. Signing out in one leaves the other signed in; «Выйти на остальных
// устройствах» on the profile signs the other browser out live — its page shows the sign-in form without a click or a
// reload — and a copy of its cookie no longer works.

test('sessions: signing out on one device keeps the other; «Выйти на остальных устройствах» signs it out live', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No players.');

  // Two devices, and a third that will sign out by itself
  const phone = await signIn(browser, vasya);
  const desktop = await signIn(browser, vasya);
  const tablet = await signIn(browser, vasya);

  await tablet.getByTestId('user-menu').click();
  await tablet.getByTestId('logout').click();
  await expect(tablet.getByTestId('login-submit')).toBeVisible();

  // The other two still work
  await phone.reload();
  await expect(phone.getByTestId('user-menu')).toBeVisible();

  // The desktop's profile lists this device first and the others (the phone; the test's own API sign-ins too)
  await desktop.goto(`/users/${vasya.id}`);
  const sessions = desktop.getByTestId('sessions');
  await expect(sessions.getByTestId('session-current')).toBeVisible();
  await expect(sessions.getByTestId('session-other').first()).toBeVisible();
  const cookies = await phone.context().cookies();

  await sessions.getByTestId('sessions-end-others').click();
  await expect(sessions).toContainText('Готово: вышли на');
  await expect(sessions.getByTestId('sessions-only-this')).toBeVisible();

  // The phone hears it without a click: its live connection closes and the page goes to the sign-in form
  await expect(phone.getByTestId('login-submit')).toBeVisible({ timeout: 20_000 });

  // A copy of the phone's cookie from before is refused too; the desktop goes on
  const copy = await browser.newContext();
  await copy.addCookies(cookies);
  const answer = await copy.request.get('/api/auth/me');
  expect(answer.status()).toBe(401);
  await desktop.reload();
  await expect(desktop.getByTestId('user-menu')).toBeVisible();

  await copy.close();
  for (const page of [phone, desktop, tablet]) await page.context().close();
});
