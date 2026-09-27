import { expect, test, type Browser } from '@playwright/test';
import { seedAdmin } from '../support/api.ts';
import { cellOf, completeHere, rollAndStartHere, signIn as signInAs } from '../support/world.ts';

// Development seed accounts (src/GameEvent.Web/Hosting/DevSeed.cs) share the dev-only DevSeed:Password
async function signIn(browser: Browser, login: string) {
  const page = await signInAs(browser, { login, password: seedAdmin.password });
  await expect(page.getByTestId('turn')).toBeVisible();
  return page;
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
  await rollAndStartHere(player);
  await completeHere(player);

  // The dice are shown to the actor, and the token has moved
  await expect(player.getByTestId('last-dice')).toBeVisible();
  await expect(player.getByTestId('roll')).toBeVisible();

  // Petya's page follows without a reload
  await expect.poll(() => cellOf(watcher, actor.name), { timeout: 10_000 }).not.toBe(before);
});
