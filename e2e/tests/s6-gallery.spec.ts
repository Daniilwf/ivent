import { readFileSync } from 'node:fs';
import { expect, test, type Locator } from '@playwright/test';
import { commandId, type Schemas } from '../support/api.ts';
import { apiOf, live, setUpSeason, signIn, type Player } from '../support/world.ts';

const board = readFileSync(new URL('../../content/map.demo.background.png', import.meta.url));
const pictures = [
  'chromium-desktop/map.png',
  'chromium-desktop/graph.png',
  'chromium-desktop/map-background.png',
  'chromium-phone/map.png',
] as const;
const picture = (index: 0 | 1 | 2 | 3) =>
  readFileSync(new URL(`../visual/__screenshots__/${pictures[index]}`, import.meta.url));

const caption = 'Нашли короткий путь в последний момент';
const openMoment = (parent: Locator) =>
  parent.getByRole('button', { name: `Открыть момент: ${caption}` });

async function seedMoments(
  player: Player,
  path: string,
  moments: { caption: string; file: Buffer }[],
) {
  const api = await apiOf(player);
  try {
    const token = await api.get<Schemas['AntiforgeryToken']>('/api/auth/antiforgery');
    for (const { caption, file } of moments) {
      const uploaded = await api.context.post(path, {
        multipart: {
          commandId: commandId(),
          itemId: commandId(),
          caption,
          file: { name: 'moment.png', mimeType: 'image/png', buffer: file },
        },
        headers: { [token.headerName]: token.token },
      });
      expect(uploaded.status(), await uploaded.text()).toBe(201);
    }
  } finally {
    await api.dispose();
  }
}

test('gallery publishes and filters moments, restores focus, and removes hidden pictures for everyone', async ({
  browser,
}, info) => {
  const world = await setUpSeason({
    players: ['Лиса', 'Сова', 'Игрок с очень длинным именем'],
    spectator: true,
  });
  const [fox, owl, withoutMoments] = world.players;
  if (!fox || !owl || !withoutMoments || !world.spectator)
    throw new Error('The gallery needs its test accounts.');

  const path = `/seasons/${world.seasonId}/gallery`;
  const seasonPath = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const rules = await world.adminApi.get<Schemas['RulesView']>(`${seasonPath}/rules`);
  await world.adminApi.put(`${adminPath}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: { ...rules.ruleset.features, gallery: true, reactions: true },
    },
  });
  const [playerPage, spectatorPage, adminPage] = await Promise.all([
    signIn(browser, fox),
    signIn(browser, world.spectator),
    signIn(browser, world.admin),
  ]);
  await Promise.all([playerPage.goto(path), spectatorPage.goto(path), adminPage.goto(path)]);
  const player = playerPage.getByRole('main');
  const spectator = spectatorPage.getByRole('main');
  const admin = adminPage.getByRole('main');
  const add = player.getByRole('button', { name: 'Добавить момент', exact: true });
  await expect(player.getByRole('heading', { name: 'Моментов пока нет' })).toBeVisible();
  await expect(spectator.getByRole('button', { name: 'Добавить момент' })).toHaveCount(0);
  await expect(admin.getByRole('button', { name: 'Добавить момент' })).toHaveCount(0);

  await add.click();
  const upload = playerPage.getByRole('dialog', { name: 'Добавить момент' });
  await upload.getByRole('button', { name: 'Опубликовать' }).click();
  await expect(upload.getByText('Выбери скриншот.')).toBeVisible();
  await upload.getByLabel('Выбрать скриншот').setInputFiles({
    name: 'map.png',
    mimeType: 'image/png',
    buffer: board,
  });
  await upload.getByLabel('Подпись').fill(caption);
  await upload.getByRole('button', { name: 'Опубликовать' }).click();
  await expect(upload).toHaveCount(0);
  if (info.project.name === 'desktop') await expect(add).toBeFocused();
  await expect(player.getByText('Момент опубликован.')).toBeVisible();

  await seedMoments(fox, `/api${path}`, [
    { caption: 'Замок сразу после финиша', file: picture(0) },
    { caption: 'Короткая дорога через лес', file: picture(2) },
  ]);
  await seedMoments(owl, `/api${path}`, [
    { caption: 'Проверили, куда ведёт развилка', file: picture(1) },
    { caption: 'Карта в последнем раунде', file: picture(3) },
    { caption: 'Поворот у финиша. '.repeat(12), file: board },
  ]);
  const cards = player.getByRole('list', { name: 'Галерея моментов' }).getByRole('button');
  await expect(cards).toHaveCount(6, live);
  const width = info.project.name === 'phone' ? 390 : 1440;
  const height = info.project.name === 'phone' ? 844 : 900;
  expect(playerPage.viewportSize()).toEqual({ width, height });
  expect(await playerPage.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
  await playerPage.evaluate(() => document.fonts.ready);
  const gridShot = info.outputPath('gallery-grid.png');
  await playerPage.screenshot({ path: gridShot, scale: 'css' });
  await info.attach('gallery grid', { path: gridShot, contentType: 'image/png' });

  const byPlayer = player.getByLabel('Игрок', { exact: true });
  await byPlayer.selectOption(withoutMoments.playerId);
  await expect(
    player.getByRole('heading', { name: 'У этого игрока пока нет моментов' }),
  ).toBeVisible();
  await player.getByRole('button', { name: 'Показать всех игроков' }).click();
  await expect(cards).toHaveCount(6);
  await byPlayer.selectOption(fox.playerId);
  await expect(cards).toHaveCount(3);

  let revealPicture!: () => void;
  const pictureResponse = new Promise<void>((resolve) => {
    revealPicture = resolve;
  });
  await playerPage.route(
    '**/gallery/*/image',
    async (route) => {
      await pictureResponse;
      await route.continue();
    },
    { times: 1 },
  );
  const card = openMoment(player);
  if (info.project.name === 'phone') await card.tap();
  else await card.press('Enter');
  const detail = playerPage.getByRole('dialog', { name: 'Просмотр момента' });
  const fullImage = detail.getByRole('img', { name: 'Скриншот игрока Лиса' });
  await expect(fullImage).toBeVisible();
  const close = detail.getByRole('button', { name: 'Закрыть', exact: true });
  const duringDownload = await close.boundingBox();
  revealPicture();
  expect(
    await fullImage.evaluate((element: HTMLImageElement) =>
      element.decode().then(() => element.naturalWidth),
    ),
  ).toBeGreaterThan(0);
  expect(await close.boundingBox()).toEqual(duringDownload);
  const imagePath = await fullImage.getAttribute('src');
  if (!imagePath) throw new Error('The full gallery image must have a URL.');
  await expect(detail.getByRole('button', { name: 'Скрыть момент' })).toHaveCount(0);

  await openMoment(spectator).click();
  const spectatorDetail = spectatorPage.getByRole('dialog', { name: 'Просмотр момента' });
  const playerFire = detail.getByRole('button', { name: 'Реакция 🔥', exact: true });
  const spectatorFire = spectatorDetail.getByRole('button', { name: 'Реакция 🔥', exact: true });
  await spectatorFire.click();
  await expect(spectatorFire).toHaveAttribute('aria-pressed', 'true', live);
  await expect(playerFire).toHaveAccessibleDescription('1 реакция', live);
  const detailShot = info.outputPath('gallery-moment.png');
  await playerPage.screenshot({ path: detailShot, scale: 'css' });
  await info.attach('gallery moment', { path: detailShot, contentType: 'image/png' });

  await playerPage.keyboard.press('Escape');
  await expect(detail).toHaveCount(0);
  if (info.project.name === 'desktop') await expect(card).toBeFocused();

  await openMoment(admin).click();
  const adminDetail = adminPage.getByRole('dialog', { name: 'Просмотр момента' });
  await adminDetail.getByRole('button', { name: 'Скрыть момент', exact: true }).click();
  await adminPage
    .getByRole('alertdialog', { name: 'Скрыть этот момент?' })
    .getByRole('button', { name: 'Скрыть момент', exact: true })
    .click();
  await expect(adminDetail).toHaveCount(0);
  await expect(spectatorDetail).toHaveCount(0, live);
  await expect(card).toHaveCount(0, live);
  expect((await spectatorPage.request.get(imagePath)).status()).toBe(404);
  expect((await spectatorPage.request.get(imagePath.replace(/image$/, 'thumbnail'))).status()).toBe(
    404,
  );

  await world.adminApi.dispose();
});
