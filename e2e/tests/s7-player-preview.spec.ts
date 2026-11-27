import { readFileSync } from 'node:fs';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  finishedPlayerPreviewFixture,
  required,
} from '../../web/src/admin/playerPreviewFixtures.ts';
import {
  apiOf,
  complete,
  live,
  openAdmin,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signInHere,
  type World,
} from '../support/world.ts';

const picture = readFileSync(new URL('../../content/map.demo.background.png', import.meta.url));
const content = (file: string) =>
  readFileSync(new URL(`../../content/${file}`, import.meta.url), 'utf8');
const previewPath = (world: World, playerId: string) =>
  `/admin/seasons/${world.seasonId}/players/${playerId}/preview`;

async function capture(page: Page, info: TestInfo, name: string) {
  const viewport =
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
  expect(page.viewportSize()).toEqual(viewport);
  await page.evaluate(() => document.fonts.ready);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    viewport.width,
  );
  const path = info.outputPath(`${name}.png`);
  await page.screenshot({ path, scale: 'css' });
  await info.attach(name, { path, contentType: 'image/png' });
}

async function expectPreviewContextInViewport(page: Page) {
  const viewport = required(page.viewportSize());
  for (const locator of [
    page.getByTestId('preview-player-name'),
    page.getByTestId('preview-season-name'),
    page.getByText('Только просмотр', { exact: true }),
    page.getByRole('link', { name: 'Вернуться в админку', exact: true }),
  ]) {
    const box = required(await locator.boundingBox());
    expect(box.y).toBeGreaterThanOrEqual(0);
    expect(box.y + box.height).toBeLessThanOrEqual(viewport.height);
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);
  }
}

test('an admin sees scoped private game materials while a second admin tab stays independent', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Исполнитель', 'Свидетель'], start: false });
  const [owner, witness] = world.players;
  if (!owner || !witness) throw new Error('The preview needs two players.');
  const admin = `/api/admin/seasons/${world.seasonId}`;
  const season = `/api/seasons/${world.seasonId}`;
  const player = await apiOf(owner);
  try {
    const rules = await world.adminApi.get<Schemas['RulesView']>(`${season}/rules`);
    await world.adminApi.put(`${admin}/rules`, {
      expectedVersion: rules.version,
      ruleset: {
        ...rules.ruleset,
        features: { ...rules.ruleset.features, items: true, events: true },
        events: { defaultDeck: 'bad', irlProof: { maxFiles: 2 } },
      },
    });
    await world.adminApi.post(`/api/test/seasons/${world.seasonId}/content`, {
      objects: [content('events/karaoke.json'), content('items/bird-thief.json')],
      wheels: [
        JSON.stringify({
          id: 'bad',
          name: 'Плохие ивенты',
          deck: 'bad',
          entries: [{ objectId: 'karaoke', weight: 1 }],
        }),
      ],
      comment: 'Материалы для проверки просмотра',
    });
    await world.adminApi.post(`${admin}/status`, { to: 'active' });
    await rollAndStart(player, world.seasonId);
    await player.post(`${season}/drop`);
    const effect = (await seasonOf(player, world.seasonId)).me?.manualEffects[0];
    if (!effect?.irl) throw new Error('The private fixture needs an IRL task.');
    const irlFile = await player.upload('/api/files', picture, 'irl.png');
    const irlLink = `https://example.com/private-irl-${world.seasonId}`;
    await player.post(`${season}/effects/${effect.id}/irl-proof`, {
      files: [irlFile.id],
      link: irlLink,
    });
    const game = await rollAndStart(player, world.seasonId);
    await complete(player, world.seasonId);
    const run = (await seasonOf(player, world.seasonId)).me?.lastCompleted;
    if (!run) throw new Error('The private fixture needs a completed run.');
    const proofFile = await player.upload('/api/files', picture, 'credits.png');
    const proofLink = `https://example.com/private-run-${world.seasonId}`;
    await player.post(`${season}/runs/${run.id}/proof`, {
      files: [proofFile.id],
      links: [proofLink],
      note: 'Титры и финальная сцена',
      witnessId: witness.playerId,
    });
    await world.adminApi.post(`${admin}/runs/${run.id}/reject`, {
      comment: 'Нужен полный экран титров',
    });
    await world.adminApi.post(`${admin}/players/${owner.playerId}/inventory`, {
      giveObjectId: 'bird-thief',
      comment: 'Предмет для проверки просмотра',
    });
    await player.post(`${season}/roll`);

    await page.goto('/');
    await signInHere(page, world.admin);
    await openAdmin(page, world, 'players');
    const entry = page.getByTestId(`player-${owner.playerId}`).getByTestId('player-preview-open');
    await entry.focus();
    await entry.press('Enter');
    await expect(page).toHaveURL(previewPath(world, owner.playerId));
    await expect(
      page.getByRole('heading', { name: 'Просмотр глазами игрока', level: 1 }),
    ).toBeFocused();
    await expect(page.getByTestId('preview-player-name')).toHaveText(owner.name);
    await expect(page.getByText('Только просмотр', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Птичкерс', exact: true })).toBeAttached();
    const writeRequests: string[] = [];
    const externalRequests: string[] = [];
    page.on('request', (request) => {
      const url = new URL(request.url());
      if (url.pathname.startsWith('/api/') && !['GET', 'HEAD'].includes(request.method()))
        writeRequests.push(`${request.method()} ${url.pathname}`);
      if (url.hostname === 'example.com') externalRequests.push(request.url());
    });
    await expect(page.locator('form,input,textarea,select')).toHaveCount(0);
    await expect(page.getByTestId('user-menu')).toHaveCount(0);
    await expect(
      page.getByRole('button', {
        name: /Бросить|Начать|Использовать|Купить|Отправить|Выполнено|Отклонить|Принять/,
      }),
    ).toHaveCount(0);
    await capture(page, info, 'preview-offer');
    await page
      .getByRole('heading', { name: 'Караоке', exact: true })
      .first()
      .scrollIntoViewIfNeeded();
    await expect(
      page.getByRole('link', { name: `Ссылка 1: ${irlLink}`, exact: true }),
    ).toHaveAttribute('href', irlLink);
    await capture(page, info, 'preview-irl');
    await page.getByRole('heading', { name: game, exact: true }).last().scrollIntoViewIfNeeded();
    await expect(page.getByText('Титры и финальная сцена', { exact: false })).toBeVisible();
    await expect(page.getByText('Свидетель: Свидетель', { exact: true })).toBeVisible();
    await expect(page.getByText('Нужен полный экран титров', { exact: false })).toBeVisible();
    await expect(
      page.getByRole('link', { name: `Ссылка 1: ${proofLink}`, exact: true }),
    ).toHaveAttribute('href', proofLink);
    const materials = page.locator(`a[href^="/api${previewPath(world, owner.playerId)}/files/"]`);
    await expect(materials).toHaveCount(2);
    for (const image of await materials.locator('img').all()) {
      await expect(image).toHaveJSProperty('complete', true);
      expect(
        await image.evaluate((node) => (node as HTMLImageElement).naturalWidth),
      ).toBeGreaterThan(0);
    }
    await capture(page, info, 'preview-rejected-proof');

    const ordinary = await page.context().newPage();
    await openAdmin(ordinary, world, 'players');
    await expect(ordinary.getByTestId('user-menu')).toContainText(world.admin.name);
    const me = await ordinary.request.get('/api/auth/me');
    expect(await me.json()).toMatchObject({ id: world.admin.id, role: 'admin' });
    const before = await world.adminApi.get<Schemas['PlayerPreviewView']>(
      `/api${previewPath(world, owner.playerId)}`,
    );
    const refresh = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === `/api${previewPath(world, owner.playerId)}` &&
        response.status() === 200,
    );
    const row = ordinary.getByTestId(`player-${owner.playerId}`);
    await row.getByTestId('adjust-open').click();
    await row.getByTestId('adjust-coins').fill('1');
    await row.getByTestId('adjust-comment').fill('Вторая админская вкладка');
    await row.getByTestId('adjust-submit').click();
    const updated = (await (await refresh).json()) as Schemas['PlayerPreviewView'];
    expect(updated.economy?.coins).toBe((before.economy?.coins ?? 0) + 1);
    await expect(row.getByTestId('adjust-form')).toHaveCount(0);
    const otherSeasonId = crypto.randomUUID();
    const otherSeasonName = `Независимый архив ${crypto.randomUUID()}`;
    await world.adminApi.post('/api/admin/seasons', {
      seasonId: otherSeasonId,
      name: otherSeasonName,
    });
    await openAdmin(
      ordinary,
      { ...world, seasonId: otherSeasonId, seasonName: otherSeasonName },
      'players',
    );
    await expect(ordinary.getByTestId('admin-season-name').locator(':scope > span')).toHaveText(
      `Сезон: ${otherSeasonName} (готовится)`,
    );
    expect(writeRequests).toEqual([]);
    expect(externalRequests).toEqual([]);
    const exit = page.getByRole('link', { name: 'Вернуться в админку', exact: true });
    await expect(exit).toHaveAttribute('href', `/admin/players?season=${world.seasonId}`);
    const keptBeforeExit = await page.evaluate(() => localStorage.getItem('ge.admin.season'));
    await exit.click();
    await expect(page).toHaveURL(`/admin/players?season=${world.seasonId}`);
    await expect(page.getByTestId('admin-season-name').locator(':scope > span')).toHaveText(
      `Сезон: ${world.seasonName} (идёт)`,
    );
    await expect(page.getByTestId('user-menu')).toContainText(world.admin.name);
    expect(await page.evaluate(() => localStorage.getItem('ge.admin.season'))).toBe(keptBeforeExit);
    await page.reload();
    await expect(page.getByTestId('admin-season-name').locator(':scope > span')).toHaveText(
      `Сезон: ${world.seasonName} (идёт)`,
    );
    await expect(ordinary.getByTestId('admin-season-name').locator(':scope > span')).toHaveText(
      `Сезон: ${otherSeasonName} (готовится)`,
    );
    await ordinary.close();
  } finally {
    await player.dispose();
    await world.adminApi.dispose();
  }
});

test('a private preview has loading, failed and empty states with an always available exit', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Лиса'] });
  const [player] = world.players;
  if (!player) throw new Error('No preview player.');
  const path = previewPath(world, player.playerId);
  let fail = true;
  let release: (() => void) | undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  try {
    await page.route(`**/api${path}`, async (route) => {
      expect(route.request().headers()['x-gameevent-player-preview']).toBe('true');
      if (fail) await route.fulfill({ status: 503, json: {} });
      else {
        await gate;
        await route.continue();
      }
    });
    await page.goto('/');
    await signInHere(page, world.admin);
    await page.goto(path);
    await expect(page.getByRole('alert')).toBeVisible();
    await expect(
      page.getByRole('link', { name: 'Вернуться в админку', exact: true }),
    ).toBeVisible();
    await capture(page, info, 'preview-error');
    fail = false;
    await page.getByRole('button', { name: 'Попробовать ещё раз', exact: true }).click();
    await expect(page.getByTestId('loading')).toBeVisible();
    await capture(page, info, 'preview-loading');
    release?.();
    await expect(page.getByTestId('preview-player-name')).toHaveText(player.name, live);
    await expect(
      page.getByText('Нет предложения или активного прохождения.', { exact: true }),
    ).toBeVisible();
    await expect(page.locator('form,input,textarea,select')).toHaveCount(0);
    await capture(page, info, 'preview-empty');
  } finally {
    release?.();
    await world.adminApi.dispose();
  }
});

test('a dense finished snapshot keeps the player design and readable materials', async ({
  page,
}, info) => {
  const value = finishedPlayerPreviewFixture();
  const world = await setUpSeason({ players: [value.playerName] });
  const [player] = world.players;
  if (!player) throw new Error('No preview player.');
  const oldId = value.playerId;
  value.playerId = player.playerId;
  value.season.id = world.seasonId;
  required(value.season.me).playerId = player.playerId;
  required(value.season.players.find((item) => item.id === oldId)).id = player.playerId;
  required(value.season.leaderboard.find((item) => item.playerId === oldId)).playerId =
    player.playerId;
  try {
    await page.route(`**/api${previewPath(world, player.playerId)}`, (route) =>
      route.fulfill({ status: 200, json: value }),
    );
    await page.goto('/');
    await signInHere(page, world.admin);
    await page.goto(previewPath(world, player.playerId));
    await expect(page.getByTestId('my-finish')).toBeVisible();
    await expect(page.getByText('Завершён', { exact: true })).toBeVisible();
    await expect(page.locator('form,input,textarea,select')).toHaveCount(0);
    await capture(page, info, 'preview-finished');
    await page.getByRole('heading', { name: 'Инвентарь', exact: true }).scrollIntoViewIfNeeded();
    await expect(page.getByRole('heading', { name: /Предмет с длинным названием/ })).toHaveCount(
      16,
    );
    await page.getByText('Описание «Предмет с длинным названием 1»', { exact: true }).click();
    await expectPreviewContextInViewport(page);
    await capture(page, info, 'preview-dense-inventory');
    await page
      .getByRole('heading', { name: 'Прохождения и пруфы', exact: true })
      .scrollIntoViewIfNeeded();
    await expect(page.getByText('Полные титры и финальная сцена', { exact: false })).toBeVisible();
    await expectPreviewContextInViewport(page);
    await capture(page, info, 'preview-finished-proof');
  } finally {
    await world.adminApi.dispose();
  }
});
