import { fileURLToPath } from 'node:url';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  complete,
  openAdmin,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signInHere,
  type World,
} from '../support/world.ts';

async function setFeatures(world: World, changes: Partial<Schemas['Features']>) {
  const current = await world.adminApi.get<Schemas['RulesView']>(
    `/api/seasons/${world.seasonId}/rules`,
  );
  await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: current.version,
    ruleset: { ...current.ruleset, features: { ...current.ruleset.features, ...changes } },
  });
}

async function openSection(page: Page, info: TestInfo, world: World, section: string) {
  await page.goto('/');
  await signInHere(page, world.admin);
  await openAdmin(page, world, 'season');
  const link = page.getByTestId(`admin-nav-${section}`);
  if (info.project.name === 'phone') {
    await expect(link).toHaveCount(0);
    await page.getByTestId('admin-sections').click();
    await expect(page.getByRole('dialog', { name: 'Разделы админки' })).toBeVisible();
  } else {
    await expect(page.getByTestId('admin-sections')).toHaveCount(0);
  }
  await expect(link).toBeVisible();
  await link.click();
  await expect(page).toHaveURL(new RegExp(`/admin/${section}$`));
  await expect(page.getByTestId('admin-season-name')).toContainText(world.seasonName);
}

async function designShot(page: Page, info: TestInfo, name: string) {
  await page.evaluate(() => document.fonts.ready);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  const path = `../../test-artifacts/design/s5-23-admin-operations/${name}-${info.project.name}.png`;
  await page.screenshot({ path: fileURLToPath(new URL(path, import.meta.url)), fullPage: true });
}

test('the admin launches a published global event with a goal and ends it early from the browser', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Игрок'], start: false });
  const seasonPath = `/api/seasons/${world.seasonId}`;
  const objectId = 'shared-evening';
  const name = 'Общий игровой вечер';
  await setFeatures(world, { items: true, events: true, globalEvents: true });
  await world.adminApi.post(`/api/test/seasons/${world.seasonId}/content`, {
    objects: [
      JSON.stringify({
        id: objectId,
        kind: 'event',
        name,
        description: 'Вместе проходим две игры.',
        rarity: 'common',
        effect: {
          target: { selector: 'all' },
          actions: [{ type: 'changeResource', resource: 'coins', amount: 2 }],
        },
      }),
    ],
    wheels: [],
    comment: 'Общий ивент для проверки управления',
  });
  await world.adminApi.post(`/api/admin/seasons/${world.seasonId}/status`, { to: 'active' });

  try {
    await openSection(page, info, world, 'globalevents');
    const open = page.getByTestId('global-event-open-launch');
    await open.click();
    const form = page.getByRole('dialog', { name: 'Запуск общего ивента' });
    await form.getByTestId('global-event-object').selectOption(objectId);
    await form.getByTestId('global-event-duration').fill('48');
    await form.getByTestId('global-event-goal').selectOption('runs');
    await form.getByTestId('global-event-target').fill('2');
    await designShot(page, info, 'global-launch');
    await form.getByTestId('global-event-launch').click();

    const event = page.getByRole('article').filter({ has: page.getByRole('heading', { name }) });
    await expect(event).toContainText('0 из 2 прохождений');
    await expect(open).toBeFocused();
    const read = () =>
      world.adminApi.get<Schemas['GlobalEventView'][]>(`${seasonPath}/global-events`);
    expect(await read()).toMatchObject([
      { name, objectId, status: 'active', goal: { metric: 'runs', target: 2, progress: 0 } },
    ]);
    await designShot(page, info, 'global-active');

    await event.getByTestId('global-event-open-end').click();
    const confirmation = page.getByRole('alertdialog', { name: `Завершить «${name}»?` });
    await expect(confirmation.getByTestId('global-event-end-no')).toBeFocused();
    expect(await read()).toMatchObject([{ status: 'active' }]);
    await confirmation.getByTestId('global-event-end-yes').click();
    await expect(event).toContainText('Завершён');
    await expect(event.getByTestId('global-event-open-end')).toHaveCount(0);
    expect(await read()).toMatchObject([{ name, status: 'ended' }]);
  } finally {
    await world.adminApi.dispose();
  }
});

test('the admin sets a weekly challenge and marks an actual completed run from the browser', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Маша'] });
  const [player] = world.players;
  if (!player) throw new Error('No player.');
  const runner = await apiOf(player);

  try {
    await setFeatures(world, { weeklyChallenge: true });
    await rollAndStart(runner, world.seasonId);
    const activeRun = (await seasonOf(runner, world.seasonId)).me?.activeRun;
    if (!activeRun) throw new Error('No active run.');
    await complete(runner, world.seasonId);
    const runs = await world.adminApi.get<Schemas['GameRunView'][]>(
      `/api/pool/${activeRun.game.id}/runs`,
    );
    const run = runs.find((candidate) => candidate.runId === activeRun.id);
    if (!run?.completedAt) throw new Error('The finished run has no completion time.');
    // Set the Moscow start an hour before the recorded finish. Parallel cases may move the game clock.
    const typedStart = new Date(Date.parse(run.completedAt) + 2 * 60 * 60 * 1000)
      .toISOString()
      .slice(0, 16);
    const name = 'Игра недели';
    const read = () =>
      world.adminApi.get<Schemas['WeeklyChallengeView'][]>(
        `/api/seasons/${world.seasonId}/weekly-challenges`,
      );
    const playerRows = () =>
      world.adminApi.get<Schemas['AdminPlayerView'][]>(
        `/api/admin/seasons/${world.seasonId}/players`,
      );
    const before = (await playerRows()).find(({ id }) => id === player.playerId);
    if (!before) throw new Error('No player balance.');

    await openSection(page, info, world, 'weeklychallenges');
    await page.getByTestId('weekly-challenge-open-set').click();
    const setup = page.getByRole('dialog', { name: 'Новый челлендж недели' });
    await setup.getByTestId('weekly-challenge-name').fill(name);
    await setup.getByTestId('weekly-challenge-description').fill('Пройди игру и покажи финал.');
    await setup.getByTestId('weekly-challenge-starts').fill(typedStart);
    await setup.getByTestId('weekly-challenge-coins').fill('10');
    await setup.getByTestId('weekly-challenge-check').selectOption('manual');
    await designShot(page, info, 'weekly-set');
    await setup.getByTestId('weekly-challenge-set').click();

    const card = page.getByRole('article').filter({ has: page.getByRole('heading', { name }) });
    await expect(card).toContainText('Ручная проверка');
    expect(await read()).toMatchObject([
      { name, check: 'manual', rewardCoins: 10, completedBy: [] },
    ]);
    await card.getByTestId('weekly-challenge-open-mark').click();
    const marker = page.getByRole('dialog', { name: `Выполнение «${name}»` });
    await marker.getByRole('searchbox').fill(activeRun.game.title);
    await marker.getByTestId('weekly-challenge-game').selectOption(activeRun.game.id);
    await marker.getByTestId('weekly-challenge-run').selectOption(run.runId);
    await designShot(page, info, 'weekly-mark');
    await marker.getByTestId('weekly-challenge-mark').click();

    await expect(card.getByText(player.name, { exact: true })).toBeVisible();
    expect(await read()).toMatchObject([
      { completedBy: [{ playerId: player.playerId, name: player.name }] },
    ]);
    expect((await playerRows()).find(({ id }) => id === player.playerId)?.coins).toBe(
      before.coins + 10,
    );
    await designShot(page, info, 'weekly-completed');
  } finally {
    await runner.dispose();
    await world.adminApi.dispose();
  }
});
