import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { live, setUpSeason, signIn } from '../support/world.ts';

test('gallery access and the private recap follow their own season flags', async ({ browser }) => {
  const world = await setUpSeason({ players: ['Лиса', 'Барсук'], spectator: true });
  const [player] = world.players;
  const spectator = world.spectator;
  if (!player || !spectator) throw new Error('A player and a spectator are required.');

  const [playerPage, spectatorPage] = await Promise.all([
    signIn(browser, player),
    signIn(browser, spectator),
  ]);
  const profile = `/users/${player.id}`;
  const recapPath = `/seasons/${world.seasonId}/recap`;
  const rulesPath = `/api/seasons/${world.seasonId}/rules`;
  const galleryNav = playerPage.getByTestId('nav-gallery');
  const recapName = `Твой сезон, ${world.seasonName}`;
  const recapLink = playerPage.getByRole('link', { name: recapName, exact: true });

  async function setFlags(gallery: boolean, seasonStatistics: boolean) {
    const rules = await world.adminApi.get<Schemas['RulesView']>(rulesPath);
    await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
      expectedVersion: rules.version,
      ruleset: {
        ...rules.ruleset,
        features: { ...rules.ruleset.features, gallery, seasonStatistics, polls: true },
      },
    });
  }

  await setFlags(true, false);
  await playerPage.goto(profile);
  await expect(playerPage.getByTestId('profile-seasons')).toBeVisible();
  await expect(galleryNav).toBeVisible(live);
  await expect(playerPage.getByTestId('nav-polls')).toBeVisible();
  const navigation = playerPage.getByTestId('site-nav');
  await expect
    .poll(() => navigation.evaluate((node) => node.scrollWidth <= node.clientWidth))
    .toBe(true);
  await expect(recapLink).toHaveCount(0);
  await galleryNav.click();
  await expect(
    playerPage.getByRole('heading', { level: 1, name: 'Галерея моментов' }),
  ).toBeVisible();

  await setFlags(true, true);
  await playerPage.goto(profile);
  await expect(recapLink).toBeVisible();
  await expect(galleryNav).toBeVisible(live);
  await recapLink.click();
  await expect(playerPage).toHaveURL(new RegExp(`${recapPath}$`));
  await expect(playerPage.getByRole('heading', { level: 1, name: 'Твой сезон' })).toBeVisible();
  const exportButton = playerPage.getByRole('button', { name: 'Скачать картинку для чата' });
  await expect(exportButton).toBeEnabled();

  await spectatorPage.goto(profile);
  await expect(spectatorPage.getByTestId('profile-seasons')).toBeVisible();
  await expect(spectatorPage.getByRole('link', { name: recapName, exact: true })).toHaveCount(0);
  await spectatorPage.goto(recapPath);
  await expect(spectatorPage.getByRole('link', { name: 'К моему профилю' })).toBeVisible();
  await expect(
    spectatorPage.getByRole('button', { name: 'Скачать картинку для чата' }),
  ).toHaveCount(0);

  await setFlags(false, true);
  await playerPage.goto(profile);
  await expect(recapLink).toBeVisible();
  await expect(galleryNav).toHaveCount(0);
  await recapLink.click();
  await expect(exportButton).toBeEnabled();
});
