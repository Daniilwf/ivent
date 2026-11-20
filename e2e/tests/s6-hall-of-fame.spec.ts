import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { PNG } from 'pngjs';
import type { Api, Schemas } from '../support/api.ts';
import {
  apiOf,
  complete,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
  uniqueTag,
  type Account,
} from '../support/world.ts';

async function addSeason(admin: Api, name: string, players: Account[]) {
  const seasonId = crypto.randomUUID();
  await admin.post('/api/admin/seasons', { seasonId, name });
  for (const player of players) {
    await admin.post(`/api/admin/seasons/${seasonId}/players`, { userId: player.id });
  }
  return seasonId;
}

async function capture(page: Page, info: TestInfo, name: string) {
  const viewport =
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
  expect(page.viewportSize()).toEqual(viewport);
  await page.evaluate(() => document.fonts.ready);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    viewport.width,
  );
  const path = info.outputPath(`${name}.png`);
  const image = PNG.sync.read(await page.screenshot({ path, scale: 'css' }));
  expect({ width: image.width, height: image.height }).toEqual(viewport);
  await info.attach(name, { path, contentType: 'image/png' });
}

test('a spectator opens the hall from navigation and reads archived winners and records', async ({
  browser,
}, info) => {
  const tag = uniqueTag().slice(-6);
  const world = await setUpSeason({
    players: [`Следопыт ${tag}`, `Архивариус ${tag}`],
    spectator: true,
  });
  const [player, retired] = world.players;
  if (!player || !retired || !world.spectator) throw new Error('Missing test accounts.');

  const { adminApi: admin, seasonId } = world;
  try {
    const seasonAdmin = `/api/admin/seasons/${seasonId}`;
    const { ruleset: openingRules } = await admin.get<Schemas['RulesView']>(
      `/api/seasons/${seasonId}/rules`,
    );
    await admin.post(`${seasonAdmin}/players/${player.playerId}/adjust`, {
      cellId: `c${String(openingRules.map.linearLength - 1)}`,
      comment: 'Одна клетка до финиша',
    });
    const actor = await apiOf(player);
    let run: Schemas['CompletedRunView'] | null | undefined;
    try {
      await rollAndStart(actor, seasonId);
      await complete(actor, seasonId);
      const { me } = await seasonOf(actor, seasonId);
      expect(me?.finish).toBeTruthy();
      run = me?.lastCompleted;
    } finally {
      await actor.dispose();
    }
    if (!run) throw new Error('The game completion is missing.');
    // At the accepted limit, our run is visible even when other browser fixtures finish concurrently.
    await admin.post(`${seasonAdmin}/runs/${run.id}/hours`, {
      hours: 1000,
      comment: 'Продолжительность для проверки рекорда',
    });
    await admin.post(`${seasonAdmin}/status`, { to: 'closing' });
    await admin.post(`${seasonAdmin}/runs/${run.id}/approve`, { comment: 'Прохождение проверено' });
    await admin.post(`${seasonAdmin}/status`, { to: 'finished' });

    for (const name of [
      'Осенний марафон прохождений на длинных вечерах за настольными играми',
      'Весенний турнир исследователей редких игровых миров с друзьями',
    ]) {
      const id = await addSeason(admin, `${name} ${tag}`, [player]);
      for (const status of ['active', 'closing', 'finished']) {
        await admin.post(`/api/admin/seasons/${id}/status`, { to: status });
      }
    }

    const archiveName = `Большой летний сезон настольных приключений и хранителей игрового архива ${tag}`;
    const archiveId = await addSeason(admin, archiveName, [player, retired]);
    const archiveAdmin = `/api/admin/seasons/${archiveId}`;
    const { version, ruleset } = await admin.get<Schemas['RulesView']>(
      `/api/seasons/${archiveId}/rules`,
    );
    await admin.put(`${archiveAdmin}/rules`, {
      expectedVersion: version,
      ruleset: { ...ruleset, ranking: { ...ruleset.ranking, tiebreakers: [] } },
    });
    await admin.post(`${archiveAdmin}/status`, { to: 'active' });
    const players = await admin.get<Schemas['AdminPlayerView'][]>(`${archiveAdmin}/players`);
    for (const firstPlace of players) {
      await admin.post(`${archiveAdmin}/players/${firstPlace.id}/adjust`, {
        comment: 'Равные результаты сезона',
        pointsDelta: 17,
      });
    }
    for (const status of ['closing', 'finished', 'archived']) {
      await admin.post(`${archiveAdmin}/status`, { to: status });
    }
    await admin.post(`/api/admin/accounts/${retired.id}/delete`);

    const draftName = `Черновик ${tag}`;
    const closingName = `Закрывается ${tag}`;
    await addSeason(admin, draftName, [player]);
    const closingId = await addSeason(admin, closingName, [player]);
    await admin.post(`/api/admin/seasons/${closingId}/status`, { to: 'active' });
    await admin.post(`/api/admin/seasons/${closingId}/status`, { to: 'closing' });

    const page = await signIn(browser, world.spectator);
    try {
      const nav = page.getByTestId('site-nav');
      const navLink = nav.getByRole('link', { name: 'Зал славы', exact: true });
      const [recordResponse] = await Promise.all([
        page.waitForResponse(
          (response) => new URL(response.url()).pathname === '/api/hall-of-fame/records',
        ),
        navLink.press('Enter'),
      ]);
      expect(recordResponse.ok()).toBe(true);
      const { longestGames, fastestFinishes } =
        (await recordResponse.json()) as Schemas['HallOfFameRecordsView'];
      expect(longestGames.some((record) => record.runId === run.id)).toBe(true);
      expect(fastestFinishes.length).toBeGreaterThan(0);
      await expect(page).toHaveURL(/\/hall-of-fame$/);
      await expect(navLink).toHaveAttribute('aria-current', 'page');
      await expect(page.getByRole('heading', { name: 'Зал славы', level: 1 })).toBeFocused();
      expect(await nav.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(
        true,
      );

      const winners = page.getByRole('region', { name: 'Победители сезонов' });
      const archived = winners.locator('ol > li').filter({
        has: page.getByRole('heading', { name: archiveName, level: 3, exact: true }),
      });
      await expect(archived.getByText('Общее первое место', { exact: true })).toBeVisible();
      await expect(archived.getByText('17 очков', { exact: true })).toHaveCount(2);
      await expect(archived.getByRole('link', { name: player.name, exact: true })).toHaveAttribute(
        'href',
        `/users/${player.id}`,
      );
      await expect(archived.getByText(retired.name, { exact: true })).toBeVisible();
      await expect(archived.getByRole('link', { name: retired.name, exact: true })).toHaveCount(0);
      await expect(
        winners.getByRole('heading', { name: world.seasonName, exact: true }),
      ).toBeVisible();
      for (const name of [draftName, closingName]) {
        await expect(winners.getByRole('heading', { name, exact: true })).toHaveCount(0);
      }

      const records = page.getByRole('region', { name: 'Самая длинная игра' });
      await expect(records.getByText('1 000 ч', { exact: true }).first()).toBeVisible();
      const gameLinks = records.getByRole('link');
      await expect(gameLinks).toHaveText(longestGames.map(({ title }) => title));
      expect(
        await gameLinks.evaluateAll((links) => links.map((link) => link.getAttribute('href'))),
      ).toEqual(longestGames.map(({ gameId }) => `/games/${gameId}`));
      await capture(page, info, 'hall-of-fame');

      const jump = page.getByRole('link', { name: 'К рекордам', exact: true });
      if (info.project.name === 'phone') {
        const recordHeading = records.getByRole('heading', { level: 2 });
        await expect(recordHeading).not.toBeInViewport();
        await jump.press('Enter');
        await expect(recordHeading).toBeFocused();
        await page.evaluate(
          () =>
            new Promise<void>((resolve) =>
              requestAnimationFrame(() =>
                requestAnimationFrame(() => {
                  resolve();
                }),
              ),
            ),
        );
        await expect(recordHeading).toBeInViewport();
        const headerBottom = await nav.evaluate(
          (element) => element.closest('header')?.getBoundingClientRect().bottom ?? 0,
        );
        expect((await recordHeading.boundingBox())?.y).toBeGreaterThanOrEqual(headerBottom);
        await capture(page, info, 'hall-of-fame-record');
      } else {
        await expect(jump).toBeHidden();
      }

      const fastest = page.getByRole('region', { name: 'Самый быстрый финиш' });
      const fastestHeading = fastest.getByRole('heading', { level: 2 });
      await fastestHeading.evaluate((heading) => {
        heading.scrollIntoView({ block: 'start' });
      });
      await expect(fastestHeading).toBeInViewport();
      await expect(fastest.getByRole('listitem')).toHaveCount(fastestFinishes.length);
      for (const finish of fastestFinishes) {
        const row = fastest.getByRole('listitem').filter({ hasText: finish.seasonName });
        await expect(row.getByText(finish.name, { exact: true })).toBeVisible();
      }
      await capture(page, info, 'hall-of-fame-fastest');

      await gameLinks
        .and(records.locator(`a[href="/games/${run.game.id}"]`))
        .first()
        .click();
      await expect(page).toHaveURL(new RegExp(`/games/${run.game.id}$`));
      await expect(page.getByRole('heading', { name: run.game.title, level: 1 })).toBeVisible();
    } finally {
      await page.context().close();
    }
  } finally {
    await admin.dispose();
  }
});
