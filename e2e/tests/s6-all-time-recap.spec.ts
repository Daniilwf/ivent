import { expect, test } from '@playwright/test';
import {
  apiOf,
  complete,
  live,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

test('only the owner sees current and flag-off archived totals and the active reject updates them live', async ({
  browser,
}, info) => {
  const world = await setUpSeason({
    players: ['Хранитель_настольного_сезона'],
    spectator: true,
  });
  const [player] = world.players;
  if (!player || !world.spectator) throw new Error('Missing test accounts.');

  const admin = world.adminApi;
  const roller = await apiOf(player);
  try {
    const archive = world.seasonId;
    const archiveAdmin = `/api/admin/seasons/${archive}`;
    const archivedRun = await completeRun(archive, 3);
    await admin.post(`${archiveAdmin}/status`, { to: 'closing' });
    await admin.post(`${archiveAdmin}/runs/${archivedRun}/approve`, {
      comment: 'Зачтено для архивной сводки',
    });
    await admin.post(`${archiveAdmin}/status`, { to: 'finished' });
    await admin.post(`${archiveAdmin}/status`, { to: 'archived' });
    const oldRecap = await roller.context.get(`/api/seasons/${archive}/my-statistics`);
    expect(oldRecap.status()).toBe(404);

    const active = crypto.randomUUID();
    const activeAdmin = `/api/admin/seasons/${active}`;
    await admin.post('/api/admin/seasons', {
      seasonId: active,
      name: `Продолжение ${world.seasonName}`,
    });
    await admin.post(`${activeAdmin}/players`, { userId: player.id });
    await admin.post(`${activeAdmin}/status`, { to: 'active' });
    const activeRun = await completeRun(active, 6);

    const ownerPage = await signIn(browser, player);
    const spectatorPage = await signIn(browser, world.spectator);
    try {
      const profilePath = `/users/${player.id}`;
      await ownerPage.goto(profilePath);
      const recap = ownerPage.getByRole('region', { name: 'Сводка за всё время' });
      await expect(recap.getByText('9 ч', { exact: true })).toBeVisible();
      await expect(recap.getByText('Самый удачный', { exact: true })).toBeVisible();

      const size =
        info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
      expect(ownerPage.viewportSize()).toEqual(size);
      await ownerPage.evaluate(() => document.fonts.ready);
      expect(
        await ownerPage.evaluate(() => document.documentElement.scrollWidth),
      ).toBeLessThanOrEqual(size.width);
      const path = info.outputPath('profile-all-time-recap.png');
      await ownerPage.screenshot({ path, scale: 'css' });
      await info.attach('profile all-time recap', { path, contentType: 'image/png' });

      const ownRecapRequests: string[] = [];
      spectatorPage.on('request', (request) => {
        const pathname = new URL(request.url()).pathname;
        if (pathname === '/api/me/statistics') ownRecapRequests.push(pathname);
      });
      await spectatorPage.goto(profilePath);
      await expect(spectatorPage.getByRole('heading', { name: player.name })).toBeVisible();
      await expect(spectatorPage.getByRole('region', { name: 'Сводка за всё время' })).toHaveCount(
        0,
      );
      expect(ownRecapRequests).toHaveLength(0);

      await admin.post(`${activeAdmin}/runs/${activeRun}/reject`, {
        comment: 'Активный реджект проверяет обновление профиля',
      });
      await expect(recap.getByText('3 ч', { exact: true })).toBeVisible(live);
      await expect(recap.getByText('9 ч', { exact: true })).toHaveCount(0);
    } finally {
      await ownerPage.context().close();
      await spectatorPage.context().close();
    }
  } finally {
    await roller.dispose();
    await admin.dispose();
  }

  async function completeRun(seasonId: string, hours: number) {
    await rollAndStart(roller, seasonId);
    await complete(roller, seasonId, 'normal', { review: { rating: 8 } });
    const run = (await seasonOf(roller, seasonId)).me?.lastCompleted;
    if (!run) throw new Error('The game completion is missing.');
    await admin.post(`/api/admin/seasons/${seasonId}/runs/${run.id}/hours`, {
      hours,
      comment: 'Часы для сводки за всё время',
    });
    return run.id;
  }
});
