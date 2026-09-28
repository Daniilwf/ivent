import { expect, test } from '@playwright/test';
import {
  apiOf,
  commandId,
  complete,
  leaderRow,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../../support/world.ts';

// TESTING.md scenario 11 «Зритель» (SPEC «Роль «зритель»»): the spectator sees everything public — the season with its
// map and leaderboard, the feed, a player's profile and review, the game's page, the pool, the rules — and has not one
// game action: no buttons on the screens, 403 from the API. A spectator opens the newest season by default (D-18), so
// this scenario runs after the parallel ones, when its season is the newest (tests/late, D-232).

test('spectator: sees everything public and cannot make a single game action', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'], spectator: true });
  const [vasya, petya] = world.players;
  const spectator = world.spectator;
  if (!vasya || !petya || !spectator) throw new Error('No players.');

  // Setup: Vasya completed a game with a review; Petya is playing his
  const vasyaApi = await apiOf(vasya);
  const game = await rollAndStart(vasyaApi, world.seasonId);
  await complete(vasyaApi, world.seasonId, 'normal', {
    review: { rating: 9, text: 'Зрителям понравится' },
  });
  const petyaApi = await apiOf(petya);
  await rollAndStart(petyaApi, world.seasonId);
  const view = await seasonOf(vasyaApi, world.seasonId);
  const run = view.me?.lastCompleted;
  if (!run) throw new Error('No completed run.');

  // The season: its name, the spectator's card, the map and the leaderboard, no action
  const page = await signIn(browser, spectator);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(world.seasonName);
  await expect(page.getByTestId('turn')).toContainText('Ты смотришь сезон как зритель.');
  await expect(page.getByTestId('cells')).toContainText('Вася');
  await expect(page.getByTestId('cells')).toContainText('Петя');
  await expect(leaderRow(page, vasya.playerId)).toContainText(`1. Вася: ${String(run.total)} очк.`);
  for (const action of ['roll', 'start', 'complete-form', 'drop', 'tech-reroll', 'proof-form'])
    await expect(page.getByTestId(action), action).toHaveCount(0);

  // The feed: Vasya's completion, and from it his profile with the review and the game's page
  await page.getByTestId('nav-feed').first().click();
  const feed = page.getByTestId('feed');
  await expect(feed).toContainText(`Вася проходит ${game}`);
  await feed.getByRole('link', { name: 'Вася' }).first().click();
  await expect(page.getByTestId('profile')).toBeVisible();
  await expect(page.getByTestId('profile-reviews')).toContainText('Зрителям понравится');
  await page.getByTestId('profile-reviews').getByRole('link', { name: game }).first().click();
  await expect(page.getByTestId('game')).toContainText(game);
  await expect(page.getByTestId('game-runs')).toContainText('Вася');

  // The pool and the rules: read, never add
  await page.getByTestId('nav-pool').first().click();
  await expect(page.getByTestId('pool-list')).toBeVisible();
  await expect(page.getByTestId('add-game-open')).toHaveCount(0);
  await page.getByTestId('nav-rules').first().click();
  await expect(page.getByTestId('rules-dice')).toBeVisible();

  // No way to the admin pages, and the API refuses every game action
  await page.getByTestId('user-menu').click();
  await expect(page.getByTestId('to-admin')).toHaveCount(0);
  await page.keyboard.press('Escape');
  const api = await apiOf(spectator);
  const season = `/api/seasons/${world.seasonId}`;
  const actions: [string, object][] = [
    [`${season}/roll`, {}],
    [`${season}/start`, {}],
    [`${season}/drop`, {}],
    [
      `${season}/complete`,
      { difficulty: 'normal', estimatedHours: 5, hoursSource: 'HowLongToBeat' },
    ],
    [`${season}/runs/${run.id}/proof`, { links: ['https://example.com/credits.png'] }],
    [`${season}/runs/${run.id}/review`, { rating: 1 }],
    ['/api/pool', { title: 'Игра зрителя', tags: ['Инди'] }],
    [`/api/admin/seasons/${world.seasonId}/runs/${run.id}/approve`, {}],
  ];
  for (const [path, body] of actions) {
    const answer = await api.send('POST', path, { commandId: commandId(), ...body });
    expect(answer.status(), path).toBe(403);
  }
  const after = await seasonOf(vasyaApi, world.seasonId);
  expect(after.players).toEqual(view.players);
  expect(after.lastSequence).toBe(view.lastSequence);
});
