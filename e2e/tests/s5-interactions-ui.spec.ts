import { fileURLToPath } from 'node:url';
import { expect, test, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, live, rollAndStart, setUpSeason, signIn } from '../support/world.ts';

async function designShot(page: Page, name: string) {
  await page.evaluate(() => document.fonts.ready);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    await page.evaluate(() => window.innerWidth),
  );
  await page.screenshot({
    path: fileURLToPath(new URL(`../../test-artifacts/design/${name}.png`, import.meta.url)),
    fullPage: true,
    scale: 'css',
  });
}

test('two players start a duel in the browser and see it on a run and in the season rules', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Лена', 'Петя'], start: false });
  const [lena, petya] = world.players;
  if (!lena || !petya) throw new Error('Two season players are required.');
  const size = info.project.name;
  const season = `/api/seasons/${world.seasonId}`;
  const admin = `/api/admin/seasons/${world.seasonId}`;
  const rules = await world.adminApi.get<Schemas['RulesView']>(`${season}/rules`);
  const duelsRules = rules.ruleset.duels;
  if (!duelsRules) throw new Error('The season rules have no duel settings.');
  await world.adminApi.put(`${admin}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: {
        ...rules.ruleset.features,
        coop: true,
        duels: true,
        partnerBoard: true,
        weeklyChallenge: true,
        suggestions: true,
        polls: true,
        items: true,
        events: true,
        globalEvents: true,
      },
      duels: { ...duelsRules, rewardCoins: 17 },
    },
  });
  await world.adminApi.post(`${admin}/status`, { to: 'active' });

  const first = await signIn(browser, lena);
  const second = await signIn(browser, petya);
  const path = `/seasons/${world.seasonId}/interactions`;
  for (const page of [first, second]) {
    const link = page.getByTestId('season-interactions');
    await expect(link).toHaveAttribute('href', path);
    await link.click();
    await expect(page.getByRole('heading', { name: 'Взаимодействия', level: 1 })).toBeVisible();
  }
  await first.getByLabel('Соперник', { exact: true }).selectOption(petya.playerId);
  await first.getByRole('button', { name: 'Вызвать на дуэль' }).click();
  await expect(second.getByText('От кого: Лена', { exact: true })).toBeVisible(live);
  await expect(second.getByRole('button', { name: 'Принять', exact: true })).toBeEnabled();
  await designShot(second, `s5-interactions-${size}`);
  await second.getByRole('button', { name: 'Принять', exact: true }).click();
  await expect(first.getByText('Лена и Петя', { exact: true })).toBeVisible(live);

  const playerApi = await apiOf(lena);
  const duels = await playerApi.get<Schemas['DuelView'][]>(`${season}/duels`);
  expect(duels).toHaveLength(1);
  expect(duels[0]).toMatchObject({
    challengerId: lena.playerId,
    opponentId: petya.playerId,
    status: 'active',
  });
  await rollAndStart(playerApi, world.seasonId);
  await first.goto('/');
  const run = first.getByTestId('active-run');
  await expect(run.getByTestId('run-duels')).toContainText('Дуэль с игроком Петя');
  await designShot(first, `s5-run-duel-${size}`);

  await first.goto('/rules#rules-duels');
  const duelRules = first.getByRole('region', { name: 'Дуэли', exact: true });
  await expect(duelRules).toContainText('17 монеток');
  await designShot(first, `s5-rules-${size}`);
  const contents = first.getByRole('navigation', { name: 'На этой странице' });
  if (size === 'phone') await contents.getByText('На этой странице', { exact: true }).click();
  await expect(contents.getByRole('link', { name: 'Дуэли', exact: true })).toHaveAttribute(
    'href',
    '#rules-duels',
  );
  await playerApi.dispose();
});
