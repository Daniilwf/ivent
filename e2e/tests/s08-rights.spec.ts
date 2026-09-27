import { expect, test } from '@playwright/test';
import {
  apiOf,
  commandId,
  complete,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

// TESTING.md scenario 8 «Права»: a player does not open the admin pages (the address shows the game, the menu has no
// way there, the admin's API answers 403) and cannot act for another player — a proof or a review for someone else's
// run is refused, and nothing changes.

test('rights: a player neither opens the admin pages nor acts for another player', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [vasya, petya] = world.players;
  if (!vasya || !petya) throw new Error('No players.');

  // Setup: Petya has a completed run without a proof
  const petyaApi = await apiOf(petya);
  await rollAndStart(petyaApi, world.seasonId);
  await complete(petyaApi, world.seasonId);
  const before = await seasonOf(petyaApi, world.seasonId);
  const petyaRun = before.me?.lastCompleted;
  if (!petyaRun) throw new Error('No completed run.');

  // The screens: no way to the admin pages in the menu; their address shows the game
  const page = await signIn(browser, vasya);
  await page.getByTestId('user-menu').click();
  await expect(page.getByRole('menu')).toBeVisible();
  await expect(page.getByTestId('to-admin')).toHaveCount(0);
  await page.keyboard.press('Escape');
  for (const address of ['/admin', '/admin/players', '/admin/log']) {
    await page.goto(address);
    await expect(page.getByTestId('turn')).toBeVisible();
    await expect(page.getByTestId('admin')).toHaveCount(0);
  }

  // The admin's API and the test endpoints refuse the player
  const vasyaApi = await apiOf(vasya);
  const season = `/api/admin/seasons/${world.seasonId}`;
  const forbidden: [string, string, object?][] = [
    ['GET', `${season}/players`],
    ['GET', `${season}/commands`],
    ['POST', `${season}/runs/${petyaRun.id}/approve`, {}],
    ['POST', `${season}/runs/${petyaRun.id}/reject`, { comment: 'Не нравится' }],
    ['POST', `${season}/players/${vasya.playerId}/adjust`, { comment: 'Себе', pointsDelta: 100 }],
    ['POST', '/api/admin/accounts', { login: 'sneaky', name: 'Хитрый', role: 'admin' }],
    ['POST', '/api/test/clock', { advanceMinutes: 60 }],
  ];
  for (const [method, path, body] of forbidden) {
    const answer = await vasyaApi.send(
      method as 'GET' | 'POST',
      path,
      body && { commandId: commandId(), ...body },
    );
    expect(answer.status(), `${method} ${path}`).toBe(403);
  }

  // Acting for Petya: a proof and a review on his run are refused
  for (const [path, body] of [
    [`/runs/${petyaRun.id}/proof`, { links: ['https://example.com/fake.png'] }],
    [`/runs/${petyaRun.id}/review`, { rating: 1, text: 'Чужой отзыв' }],
  ] as const) {
    const answer = await vasyaApi.send('POST', `/api/seasons/${world.seasonId}${path}`, {
      commandId: commandId(),
      ...body,
    });
    expect(answer.status(), path).toBe(409);
    expect(((await answer.json()) as { code?: string }).code).toBe('run.notYours');
  }

  // Nothing changed for anyone
  const after = await seasonOf(petyaApi, world.seasonId);
  expect(after.me?.lastCompleted?.proof).toBeNull();
  expect(after.me?.lastCompleted?.review).toBeNull();
  expect(after.players).toEqual(before.players);
});
