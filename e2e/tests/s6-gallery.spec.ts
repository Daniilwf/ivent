import { readFileSync } from 'node:fs';
import { expect, test, type Locator } from '@playwright/test';
import { commandId, type Schemas } from '../support/api.ts';
import {
  apiOf,
  complete,
  live,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
  type Player,
} from '../support/world.ts';

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

test('the author chooses one approved proof frame and withdraws it in the archive', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Лиса'], spectator: true });
  const [owner] = world.players;
  if (!owner || !world.spectator)
    throw new Error('The proof moment needs an owner and a spectator.');
  const season = `/api/seasons/${world.seasonId}`;
  const adminSeason = `/api/admin/seasons/${world.seasonId}`;
  const galleryPath = `/seasons/${world.seasonId}/gallery`;
  const actor = await apiOf(owner);
  const [page, viewer] = await Promise.all([
    signIn(browser, owner),
    signIn(browser, world.spectator),
  ]);
  try {
    const rules = await world.adminApi.get<Schemas['RulesView']>(`${season}/rules`);
    await world.adminApi.put(`${adminSeason}/rules`, {
      expectedVersion: rules.version,
      ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, gallery: true } },
    });
    await rollAndStart(actor, world.seasonId);
    await complete(actor, world.seasonId);
    const run = (await seasonOf(actor, world.seasonId)).me?.lastCompleted;
    if (!run) throw new Error('An approved run is required.');
    const first = await actor.upload('/api/files', picture(0), 'first.png');
    const selected = await actor.upload('/api/files', picture(1), 'second.png');
    await actor.post(`${season}/runs/${run.id}/proof`, {
      links: [],
      files: [first.id, selected.id],
    });
    await world.adminApi.post(`${adminSeason}/runs/${run.id}/approve`, {
      comment: 'Кадры проверены',
    });

    await page.goto(galleryPath);
    await page.getByRole('button', { name: 'Добавить момент' }).click();
    const upload = page.getByRole('dialog', { name: 'Добавить момент' });
    await upload.getByText('Из одобренного пруфа', { exact: true }).click();
    await expect(upload.getByRole('radio', { name: 'Из одобренного пруфа' })).toBeChecked();
    await upload.getByLabel('Одобренный кадр').selectOption(`${run.id}:${selected.id}`);
    const preview = upload.getByRole('img', { name: 'Выбранный кадр пруфа' });
    await expect(preview).toHaveAttribute('src', selected.thumbnailUrl);
    await expect
      .poll(() => preview.evaluate((image: HTMLImageElement) => image.naturalWidth))
      .toBeGreaterThan(0);
    const frameCaption = 'Титры из одобренного пруфа';
    await upload.getByLabel('Подпись').fill(frameCaption);
    await page.evaluate(() => document.fonts.ready);
    const pickerShot = info.outputPath('gallery-proof-picker.png');
    await page.screenshot({ path: pickerShot, scale: 'css' });
    await info.attach('approved frame picker', { path: pickerShot, contentType: 'image/png' });
    await upload.getByRole('button', { name: 'Опубликовать' }).click();
    await expect(upload).toHaveCount(0);

    const hidden = await actor.post<Schemas['GalleryMomentView']>(`${season}/gallery/proofs`, {
      itemId: commandId(),
      runId: run.id,
      fileId: first.id,
      caption: 'Скрытый кадр пруфа',
    });
    await world.adminApi.post(`${adminSeason}/gallery/${hidden.id}/hide`);
    for (const to of ['closing', 'finished', 'archived']) {
      await world.adminApi.post(`${adminSeason}/status`, { to });
    }
    await Promise.all([page.goto(galleryPath), viewer.goto(galleryPath)]);
    const opening = `Открыть момент: ${frameCaption}`;
    await viewer.getByRole('button', { name: opening }).click();
    const viewed = viewer.getByRole('dialog', { name: 'Просмотр момента' });
    await expect(viewed.getByRole('button', { name: 'Отозвать публикацию' })).toHaveCount(0);
    const image = await viewed
      .getByRole('img', { name: 'Скриншот игрока Лиса' })
      .getAttribute('src');
    if (!image) throw new Error('A public derivative URL was expected.');

    await page.getByRole('button', { name: opening }).click();
    await page.getByRole('button', { name: 'Отозвать публикацию' }).click();
    const confirmation = page.getByRole('alertdialog', { name: 'Отозвать публикацию кадра?' });
    await expect(confirmation).toBeVisible();
    if (info.project.name === 'phone') {
      const cancel = await confirmation.getByRole('button', { name: 'Отмена' }).boundingBox();
      const confirm = await confirmation
        .getByRole('button', { name: 'Отозвать публикацию' })
        .boundingBox();
      expect(cancel && confirm && { x: confirm.x, width: confirm.width }).toEqual(
        cancel && { x: cancel.x, width: cancel.width },
      );
    }
    const withdrawShot = info.outputPath('gallery-proof-withdrawal.png');
    await page.screenshot({ path: withdrawShot, scale: 'css' });
    await info.attach('withdrawal in archive', { path: withdrawShot, contentType: 'image/png' });
    await confirmation.getByRole('button', { name: 'Отозвать публикацию' }).click();
    await expect(page.getByText('Публикация отозвана.')).toBeVisible();
    await expect(viewed).toHaveCount(0, live);
    expect((await viewer.request.get(image)).status()).toBe(404);
    await expect(page.getByRole('button', { name: opening })).toHaveCount(0);
    const hiddenTitle = 'Твои скрытые кадры';
    const hiddenList = page.getByRole('region', { name: hiddenTitle });
    await expect(viewer.getByRole('region', { name: hiddenTitle })).toHaveCount(0);
    const withdrawHidden = hiddenList.getByRole('button', { name: 'Отозвать публикацию' });
    await expect(withdrawHidden).toHaveAccessibleDescription(/.+/);
    const hiddenShot = info.outputPath('gallery-hidden-owner.png');
    await page.screenshot({ path: hiddenShot, scale: 'css' });
    await info.attach('hidden publication controls', {
      path: hiddenShot,
      contentType: 'image/png',
    });
    await withdrawHidden.click();
    await confirmation.getByRole('button', { name: 'Отозвать публикацию' }).click();
    await expect(hiddenList).toHaveCount(0);
  } finally {
    await Promise.all([
      actor.dispose(),
      world.adminApi.dispose(),
      page.context().close(),
      viewer.context().close(),
    ]);
  }
});
