import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
import { PNG } from 'pngjs';
import type { Schemas } from '../support/api.ts';
import { apiOf, commandId, setUpSeason, signIn } from '../support/world.ts';

const unbreakable = {
  id: 'unbreakable',
  kind: 'achievement',
  name: 'Непробиваемый',
  description: 'Переживи пять атак за сезон.',
  scope: 'season',
  effect: {
    trigger: 'hostileIncoming',
    condition: { stat: 'hostileReceived', gte: 5 },
    actions: [{ type: 'changeResource', resource: 'coins', amount: 5 }],
  },
};

test('the fifth landed attack earns a badge and a completed season adds the champion title to the profile', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'], spectator: true });
  const [attacker, recipient] = world.players;
  if (!attacker || !recipient || !world.spectator) throw new Error('Incomplete season fixture.');
  const admin = world.adminApi;
  const adminSeason = `/api/admin/seasons/${world.seasonId}`;
  const season = `/api/seasons/${world.seasonId}`;
  const rules = await admin.get<Schemas['RulesView']>(`${season}/rules`);
  expect(rules.ruleset.features.achievements).toBe(false);
  await admin.put(`${adminSeason}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: { ...rules.ruleset.features, items: true, achievements: true },
    },
  });
  const birdThief: unknown = JSON.parse(
    readFileSync(new URL('../../content/items/bird-thief.json', import.meta.url), 'utf8'),
  );
  await admin.post(`${adminSeason}/content/publish`, {
    expectedVersion: 0,
    pack: { objects: [birdThief, unbreakable], wheels: [] },
    comment: 'Проверка сезонного достижения',
  });
  await admin.post(`${adminSeason}/players/${recipient.playerId}/adjust`, {
    coinsDelta: 30,
    comment: 'Монетки для проверки атак',
  });

  const player = await apiOf(attacker);
  const landAttack = async () => {
    await admin.post(`${adminSeason}/players/${attacker.playerId}/inventory`, {
      giveObjectId: 'bird-thief',
      comment: 'Предмет для проверки атаки',
    });
    const inventory = await player.get<Schemas['EconomyView']>(`${season}/economy`);
    const item = inventory.items?.find((entry) => entry.objectId === 'bird-thief');
    if (!item) throw new Error('The attacker did not receive the item.');
    const id = commandId();
    const result = await player.post(`${season}/items/${item.instanceId}/use`, {
      commandId: id,
      targetPlayerId: recipient.playerId,
    });
    expect(result.events.filter((event) => event.type === 'hostile-received')).toHaveLength(1);
    return { id, events: result.events.map((event) => event.type) };
  };

  for (let index = 0; index < unbreakable.effect.condition.gte - 1; index++) {
    expect((await landAttack()).events).not.toContain('achievement-earned');
  }

  const page = await signIn(browser, world.spectator);
  await page.goto(`/users/${recipient.id}`);
  const heading = page.getByRole('heading', { level: 1, name: recipient.name, exact: true });
  const badges = page.getByTestId('profile-achievements');
  await expect(heading).toBeVisible();
  await expect(badges).toHaveCount(0);

  const award = await landAttack();
  expect(award.events.filter((type) => type === 'achievement-earned')).toHaveLength(1);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Достижения', exact: true })).toBeVisible();
  const badge = badges.getByRole('listitem');
  await expect(badge).toHaveCount(1);
  await expect(badge.getByText(unbreakable.name, { exact: true })).toBeVisible();
  await expect(badge.getByText(unbreakable.description, { exact: true })).toBeVisible();
  const date = badge.locator('time');
  await expect(date).toHaveText(/^\d{2}\.\d{2}$/);
  await expect(
    badge.getByText(`${world.seasonName}, ${await date.innerText()}`, { exact: true }),
  ).toBeVisible();

  const viewport =
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
  expect(page.viewportSize()).toEqual(viewport);
  const capture = async (name: string) => {
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      viewport.width,
    );
    await page.evaluate(() => document.fonts.ready);
    const screenshot = info.outputPath(`${name}-${info.project.name}.png`);
    const png = PNG.sync.read(await page.screenshot({ path: screenshot, scale: 'css' }));
    expect({ width: png.width, height: png.height }).toEqual(viewport);
    await info.attach(name, { path: screenshot, contentType: 'image/png' });
  };
  await capture('seasonal-achievement');

  await admin.post(`${adminSeason}/undo`, {
    targetCommandId: award.id,
    comment: 'Проверка отката атаки и достижения',
  });
  await page.reload();
  await expect(heading).toBeVisible();
  await expect(badges).toHaveCount(0);

  const secondAchievement = {
    ...unbreakable,
    id: 'attack-survivor',
    name: 'Последний защитник рассветного рубежа',
    description: 'Выдержи пять атак за сезон и сохрани место в турнирной таблице.',
  };
  await admin.post(`${adminSeason}/content/publish`, {
    expectedVersion: 1,
    pack: { objects: [birdThief, unbreakable, secondAchievement], wheels: [] },
    comment: 'Проверка профиля с двумя сезонными достижениями',
  });
  expect((await landAttack()).events.filter((type) => type === 'achievement-earned')).toHaveLength(
    2,
  );
  await admin.post(`${adminSeason}/players/${recipient.playerId}/adjust`, {
    pointsDelta: 17,
    comment: 'Первое место для проверки титула',
  });
  for (const status of ['closing', 'finished', 'archived']) {
    await admin.post(`${adminSeason}/status`, { to: status });
  }

  await page.reload();
  await expect(badge).toHaveCount(2);
  await expect(badges.getByText(unbreakable.name, { exact: true })).toBeVisible();
  const longName = badges.getByText(secondAchievement.name, { exact: true });
  await expect(longName).toBeVisible();
  expect(await longName.evaluate((element) => element.scrollWidth)).toBeLessThanOrEqual(
    await longName.evaluate((element) => element.clientWidth),
  );
  const seasons = page.getByTestId('profile-seasons');
  const seasonLink = seasons.getByRole('link', { name: world.seasonName, exact: true });
  await expect(seasons.getByText('Чемпион сезона', { exact: true })).toBeVisible();
  await seasonLink.focus();
  await page.keyboard.press('Shift+Tab');
  await page.keyboard.press('Tab');
  await expect(seasonLink).toBeFocused();
  await expect(badges).toBeInViewport();
  await capture('profile-badges-and-champion');
  await player.dispose();
});
