import { readFileSync } from 'node:fs';
import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import { commandId, type Api, type Schemas } from '../support/api.ts';
import { holdingTheClock } from '../support/clock.ts';
import {
  advanceClock,
  apiOf,
  complete,
  live,
  openAdmin,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

const captions = [
  'Короткая дорога привела к финишу',
  'На карте внезапно появилась развилка',
] as const;
const nomination = 'Самый смешной момент';
const picture = readFileSync(new URL('../../content/map.demo.background.png', import.meta.url));
const secondPicture = readFileSync(
  new URL('../visual/__screenshots__/chromium-desktop/graph.png', import.meta.url),
);

async function publishMoments(player: Api, gallery: string) {
  const csrf = await player.get<Schemas['AntiforgeryToken']>('/api/auth/antiforgery');
  const moments = [
    { caption: captions[0], buffer: picture },
    { caption: captions[1], buffer: secondPicture },
  ];
  const ids: string[] = [];
  for (const { caption, buffer } of moments) {
    const itemId = commandId();
    const response = await player.context.post(gallery, {
      headers: { [csrf.headerName]: csrf.token },
      multipart: {
        commandId: commandId(),
        itemId,
        caption,
        file: { name: 'moment.png', mimeType: 'image/png', buffer },
      },
    });
    expect(response.status(), await response.text()).toBe(201);
    ids.push(itemId);
  }
  return ids;
}

async function designShot(page: Page, info: TestInfo, name: string) {
  expect(page.viewportSize()).toEqual(
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 },
  );
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.evaluate(() => document.fonts.ready);
  const path = info.outputPath(`${name}.png`);
  await page.screenshot({ path, scale: 'css' });
  await info.attach(name, { path, contentType: 'image/png' });
}

async function showResultsBelowHeader(title: Locator) {
  await title.evaluate((heading) => {
    const header = document.querySelector('[data-testid="site-nav"]')?.closest('header');
    const panel = heading.closest('section') ?? heading;
    const panelTop = window.scrollY + panel.getBoundingClientRect().top;
    const headerBottom = header?.getBoundingClientRect().bottom ?? 0;
    window.scrollTo(0, panelTop - headerBottom - 8);
  });
}

test('an admin opens a final ballot and an archived winner stays recorded after its material is hidden', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Лена'], spectator: true, start: false });
  const [lena] = world.players;
  if (!lena || !world.spectator)
    throw new Error('The final ballot needs a player and a spectator.');
  const admin = `/api/admin/seasons/${world.seasonId}`;
  const season = `/api/seasons/${world.seasonId}`;
  const seasonPage = `/seasons/${world.seasonId}`;
  const rules = await world.adminApi.get<Schemas['RulesView']>(`${season}/rules`);
  await world.adminApi.put(`${admin}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: {
        ...rules.ruleset.features,
        gallery: true,
        polls: true,
        seasonNominations: true,
      },
      nominations: {
        ...rules.ruleset.nominations,
        auto: ['mostRuns'],
        voted: ['funniestMoment'],
        durationHours: 1,
        minCandidates: 2,
        maxCandidates: 2,
      },
    },
  });
  await world.adminApi.post(`${admin}/status`, { to: 'active' });

  const playerApi = await apiOf(lena);
  const [adminPage, playerPage, viewerPage] = await Promise.all([
    signIn(browser, world.admin),
    signIn(browser, lena),
    signIn(browser, world.spectator),
  ]);
  try {
    await rollAndStart(playerApi, world.seasonId);
    await complete(playerApi, world.seasonId);
    const run = (await seasonOf(playerApi, world.seasonId)).me?.lastCompleted;
    if (!run) throw new Error('Expected a completed game with a proof to review.');
    const proof = await playerApi.upload('/api/files', picture, 'credits.png');
    await playerApi.post(`${season}/runs/${run.id}/proof`, { links: [], files: [proof.id] });
    const [winnerId] = await publishMoments(playerApi, `${season}/gallery`);
    if (!winnerId) throw new Error('Expected a published candidate.');
    await world.adminApi.post(`${admin}/status`, { to: 'closing' });
    await world.adminApi.post(`${admin}/runs/${run.id}/approve`, { comment: 'Пруф проверен' });

    await openAdmin(adminPage, world, 'season');
    await playerPage.goto(`${seasonPage}/polls`);
    await expect(playerPage.getByRole('heading', { name: 'Пока нет голосований' })).toBeVisible();
    const card = playerPage.getByRole('article', { name: nomination });

    await holdingTheClock(async () => {
      const prepare = adminPage.getByRole('button', { name: 'Подготовить голосование' });
      await prepare.click();
      const dialog = adminPage.getByRole('dialog', { name: 'Подготовка итогового голосования' });
      await dialog.getByLabel('Номинация').selectOption('funniestMoment');
      await dialog.getByRole('checkbox', { name: captions[0] }).check();
      await dialog.getByRole('checkbox', { name: captions[1] }).check();
      await expect(dialog.getByText('Выбрано 2 из 2')).toBeVisible();
      await designShot(adminPage, info, 'final-ballot-composer');
      await dialog.getByRole('button', { name: 'Открыть голосование' }).click();
      await expect(adminPage.getByText('Итоговое голосование открыто.')).toBeVisible();
      if (info.project.name === 'desktop') await expect(prepare).toBeFocused();

      const premature = await world.adminApi.send('POST', `${admin}/status`, {
        commandId: commandId(),
        to: 'finished',
      });
      expect(premature.status()).toBe(409);
      expect(await premature.json()).toMatchObject({ code: 'season.nominationPollsOpen' });

      await expect(card).toBeVisible(live);
      const choices = card.getByRole('group', { name: 'Твой голос' });
      await choices.getByText(captions[0], { exact: true }).click();
      await expect(choices.getByRole('radio', { name: captions[0] })).toBeChecked();
      await card.getByRole('button', { name: 'Голосовать', exact: true }).click();
      await expect(card.getByText(`Твой голос: ${captions[0]}`)).toBeVisible(live);
      await advanceClock(world.adminApi, 61);
    });

    await expect(card.getByText('Голосование закрыто')).toBeVisible(live);
    await expect(card.getByText(`Итог: ${captions[0]}`)).toBeVisible();
    await world.adminApi.post(`${admin}/status`, { to: 'finished' });
    await world.adminApi.post(`${admin}/status`, { to: 'archived' });

    await viewerPage.goto('/hall-of-fame');
    await viewerPage.getByRole('link', { name: world.seasonName, exact: true }).click();
    await expect(viewerPage).toHaveURL(new RegExp(`${seasonPage}$`));
    const resultsTitle = viewerPage.getByRole('heading', { name: 'Номинации', exact: true });
    const results = resultsTitle.locator('..');
    await expect(results.getByRole('heading', { name: 'Больше всех игр' })).toBeVisible();
    await expect(results.getByText(captions[0], { exact: true })).toBeVisible();
    const gallery = results.getByRole('link', { name: 'Открыть галерею' });
    await expect(gallery).toHaveAttribute('href', `${seasonPage}/gallery`);
    await expect(results.getByRole('link', { name: captions[0] })).toHaveCount(0);
    const thumbnail = results.locator('img');
    await expect(thumbnail).toHaveCount(1);
    const thumbnailUrl = await thumbnail.getAttribute('src');
    if (!thumbnailUrl) throw new Error('The visible result should link its thumbnail.');
    await showResultsBelowHeader(resultsTitle);
    await expect
      .poll(() => thumbnail.evaluate((image: HTMLImageElement) => image.naturalWidth))
      .toBeGreaterThan(0);
    await designShot(viewerPage, info, 'final-nomination-results');

    await gallery.focus();
    await expect(gallery).toBeFocused();
    await world.adminApi.post(`${admin}/gallery/${winnerId}/hide`);
    await expect(results.getByText('Материалы победителя скрыты.')).toBeVisible(live);
    await expect(results.getByRole('heading', { name: nomination })).toBeFocused();
    await expect(results.getByText(captions[0], { exact: true })).toHaveCount(0);
    await expect(gallery).toHaveCount(0);
    await expect(thumbnail).toHaveCount(0);
    expect((await viewerPage.request.get(thumbnailUrl)).status()).toBe(404);
    await showResultsBelowHeader(resultsTitle);
    await designShot(viewerPage, info, 'final-nomination-hidden-winner');
  } finally {
    await Promise.all([
      playerApi.dispose(),
      world.adminApi.dispose(),
      adminPage.context().close(),
      playerPage.context().close(),
      viewerPage.context().close(),
    ]);
  }
});
