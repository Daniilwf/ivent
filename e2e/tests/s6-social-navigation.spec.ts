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
  const more = playerPage.getByRole('button', { name: 'Ещё', exact: true });
  const galleryNav = playerPage.getByRole('menuitem', { name: 'Галерея', exact: true });
  const pollNav = playerPage.getByRole('menuitem', { name: 'Голосования', exact: true });
  const interactionsNav = playerPage.getByRole('menuitem', {
    name: 'Напарники и дуэли',
    exact: true,
  });
  const recapName = `Твой сезон, ${world.seasonName}`;
  const recapLink = playerPage.getByRole('link', { name: recapName, exact: true });

  async function setFlags(gallery: boolean, seasonStatistics: boolean) {
    const rules = await world.adminApi.get<Schemas['RulesView']>(rulesPath);
    await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
      expectedVersion: rules.version,
      ruleset: {
        ...rules.ruleset,
        features: {
          ...rules.ruleset.features,
          gallery,
          seasonStatistics,
          polls: true,
          coop: true,
          partnerBoard: true,
        },
      },
    });
  }

  await setFlags(true, false);
  await playerPage.goto(profile);
  await expect(playerPage.getByTestId('profile-seasons')).toBeVisible();
  const navigation = playerPage.getByTestId('site-nav');
  await expect(more).toBeVisible(live);
  await playerPage.evaluate(() => document.fonts.ready);
  const primary = navigation.getByRole('link');
  await expect(primary).toHaveText(['Сезон', 'Лента', 'Пул', 'Правила', 'Зал славы']);
  expect(
    await primary.evaluateAll(
      (links) => new Set(links.map((link) => link.getBoundingClientRect().y)).size,
    ),
  ).toBe(1);
  await expect
    .poll(() => navigation.evaluate((node) => node.scrollWidth <= node.clientWidth))
    .toBe(true);
  expect(await playerPage.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    await playerPage.evaluate(() => window.innerWidth),
  );
  const primaryRow = navigation.locator('ul');
  const device = playerPage.viewportSize();
  if (!device) throw new Error('The player viewport must be known.');
  const scrollWidth = await primaryRow.evaluate((row) => row.scrollWidth - row.clientWidth);
  if (device.width === 390) {
    expect(scrollWidth).toBeGreaterThan(0);
    await primary.nth(3).focus();
    await playerPage.keyboard.press('Tab');
    await expect(primary.last()).toBeFocused();
    await expect.poll(() => primaryRow.evaluate((row) => row.scrollLeft)).toBeGreaterThan(0);
    await expect(more).toBeInViewport();
  } else expect(scrollWidth).toBe(0);
  await expect(recapLink).toHaveCount(0);
  await more.click();
  await expect(galleryNav).toBeVisible();
  await expect(pollNav).toBeVisible();
  await expect(interactionsNav).toHaveAttribute('href', `/seasons/${world.seasonId}/interactions`);
  await galleryNav.click();
  await expect(
    playerPage.getByRole('heading', { level: 1, name: 'Галерея моментов' }),
  ).toBeVisible();
  await more.click();
  await expect(galleryNav).toHaveAttribute('aria-current', 'page');
  await expect(interactionsNav).toBeVisible();
  await playerPage.keyboard.press('Escape');

  await setFlags(true, true);
  await playerPage.goto(profile);
  await expect(recapLink).toBeVisible();
  await more.click();
  await expect(galleryNav).toBeVisible();
  await playerPage.keyboard.press('Escape');
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
  await more.click();
  await expect(pollNav).toBeVisible();
  await expect(galleryNav).toHaveCount(0);
  await playerPage.keyboard.press('Escape');
  await recapLink.click();
  await expect(exportButton).toBeEnabled();
});
