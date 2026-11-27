import { expect, test, type Locator, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  complete,
  live,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

function reactionGroup(page: Page, reviewText: string) {
  return page
    .getByTestId('game-runs')
    .getByRole('listitem')
    .filter({ hasText: reviewText })
    .getByRole('group', { name: 'Реакции на отзыв' });
}

function fire(group: Locator) {
  return group.getByRole('button', { name: 'Реакция 🔥', exact: true });
}

async function showState(button: Locator, selected: boolean, count: string) {
  await expect(button).toHaveAttribute('aria-pressed', String(selected), live);
  await expect(button).toHaveAccessibleDescription(count, live);
}

test('a player and a spectator react to an existing review and see the same counts live', async ({
  browser,
}, info) => {
  const phone = info.project.name === 'phone';
  const world = await setUpSeason({ players: ['Вася'], spectator: true });
  const [player] = world.players;
  const spectator = world.spectator;
  if (!player || !spectator) throw new Error('A player and a spectator are required.');
  const playerApi = await apiOf(player);
  const seasonPath = `/api/seasons/${world.seasonId}`;
  const reviewText = `Отзыв до появления реакций · ${world.seasonName}`;
  const rules = await world.adminApi.get<Schemas['RulesView']>(`${seasonPath}/rules`);
  const reactionOptions = rules.ruleset.social.reactions;
  if (!Array.isArray(reactionOptions)) throw new Error('The rules must publish the reaction list.');
  expect(rules.ruleset.features.reactions).toBe(false);

  const gameTitle = await rollAndStart(playerApi, world.seasonId);
  await complete(playerApi, world.seasonId, 'normal', { review: { rating: 9, text: reviewText } });
  const run = (await seasonOf(playerApi, world.seasonId)).me?.lastCompleted;
  if (!run) throw new Error('There is no completed run with a review.');

  const playerPage = await signIn(browser, player);
  const spectatorPage = await signIn(browser, spectator);
  await Promise.all([
    playerPage.goto(`/games/${run.game.id}`),
    spectatorPage.goto(`/games/${run.game.id}`),
  ]);
  const playerGroup = reactionGroup(playerPage, reviewText);
  const spectatorGroup = reactionGroup(spectatorPage, reviewText);
  for (const page of [playerPage, spectatorPage]) {
    await expect(page.getByRole('heading', { level: 1 })).toContainText(gameTitle);
    await expect(page.getByText(reviewText, { exact: true })).toBeVisible();
    await expect(reactionGroup(page, reviewText)).toHaveCount(0);
  }

  await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, reactions: true } },
  });
  await Promise.all([playerPage.reload(), spectatorPage.reload()]);
  for (const group of [playerGroup, spectatorGroup]) {
    await expect(group.getByRole('button')).toHaveCount(reactionOptions.length, live);
    await showState(fire(group), false, '0 реакций');
  }

  const playerFire = fire(playerGroup);
  const spectatorFire = fire(spectatorGroup);
  if (phone) {
    await playerFire.tap();
  } else {
    await playerFire.focus();
    await playerPage.keyboard.press('Tab');
    await expect(
      playerGroup.getByRole('button', { name: 'Реакция 😂', exact: true }),
    ).toBeFocused();
    await playerPage.keyboard.press('Shift+Tab');
    await expect(playerFire).toBeFocused();
    await playerPage.keyboard.press('Enter');
  }
  await showState(playerFire, true, '1 реакция');
  await showState(spectatorFire, false, '1 реакция');
  if (!phone) await expect(playerFire).toBeFocused();

  if (phone) await spectatorFire.tap();
  else await spectatorFire.click();
  await showState(spectatorFire, true, '2 реакции');
  await showState(playerFire, true, '2 реакции');

  // Keep the real screen as a review artifact on both project sizes.
  const viewport = phone ? { width: 390, height: 844 } : { width: 1440, height: 900 };
  expect(spectatorPage.viewportSize()).toEqual(viewport);
  expect(
    await spectatorPage.evaluate(() => document.documentElement.scrollWidth),
  ).toBeLessThanOrEqual(viewport.width);
  await spectatorPage.evaluate(() => document.fonts.ready);
  const screenshot = info.outputPath(`review-reactions-${info.project.name}.png`);
  await spectatorPage.screenshot({ path: screenshot, scale: 'css' });
  await info.attach('review reactions', { path: screenshot, contentType: 'image/png' });

  if (phone) await spectatorFire.tap();
  else await spectatorFire.click();
  await showState(spectatorFire, false, '1 реакция');
  await showState(playerFire, true, '1 реакция');

  if (phone) await playerFire.tap();
  else await playerPage.keyboard.press('Space');
  await showState(playerFire, false, '0 реакций');
  await showState(spectatorFire, false, '0 реакций');

  const currentRules = await world.adminApi.get<Schemas['RulesView']>(`${seasonPath}/rules`);
  await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: currentRules.version,
    ruleset: {
      ...currentRules.ruleset,
      features: { ...currentRules.ruleset.features, reactions: false },
    },
  });
  for (const page of [playerPage, spectatorPage]) {
    await expect(reactionGroup(page, reviewText)).toHaveCount(0, live);
    await expect(page.getByText(reviewText, { exact: true })).toBeVisible();
  }
});
