import { expect, test, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';

const seasonId = 'd8000000-0000-0000-0000-000000000001';
const userId = 'd8000000-0000-0000-0000-000000000002';
const seasonName = 'Осенний сезон';
const seasonPath = `/api/seasons/${seasonId}`;

async function signedIn(page: Page) {
  await page.route('**/api/auth/antiforgery', (route) =>
    route.fulfill({ json: { headerName: 'x-csrf-token', token: 'visual' } }),
  );
  await page.route('**/api/auth/me', (route) =>
    route.fulfill({
      json: {
        id: userId,
        login: 'player',
        name: 'Лиса с Длинной Лесной Тропы',
        role: 'player',
        mustChangePassword: false,
        avatar: null,
        token: 4,
      } satisfies Schemas['CurrentUser'],
    }),
  );
  await page.route('**/api/seasons/current', (route) => route.fulfill({ json: { id: seasonId } }));
  await page.route(`**${seasonPath}/navigation`, (route) =>
    route.fulfill({ json: { id: seasonId, pollsEnabled: true, galleryEnabled: true } }),
  );
  await page.route('**/hubs/season/negotiate**', (route) => route.fulfill({ status: 503 }));
}

async function expectScreen(page: Page, name: string, snapshot: string) {
  await expect(page.getByRole('heading', { name, level: 1, exact: true })).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(
    page.viewportSize()?.width,
  );
  await expect(page.locator('main')).toHaveScreenshot(snapshot);
}

test('gallery grid displays long captions alongside compact cards', async ({ page }) => {
  await signedIn(page);
  const createdAt = '2026-10-20T12:00:00Z';
  const first = { id: 'd8000000-0000-0000-0000-000000000003', name: 'Лиса', token: 4 };
  const second = { id: 'd8000000-0000-0000-0000-000000000004', name: 'Енот', token: 5 };
  const moments = [
    'Снова забрели на тропу коротких игр',
    'Дошли до водопада с третьей попытки',
    'Никто из нас не заметил указатель, пока не прошёл мимо него два раза',
    'Последний кадр перед финишем',
  ];
  await page.route('**/api/seasons/gallery', (route) =>
    route.fulfill({
      json: [{ id: seasonId, name: seasonName }] satisfies Schemas['GallerySeasonView'][],
    }),
  );
  await page.route(`**${seasonPath}/gallery`, (route) =>
    route.fulfill({
      json: {
        canPublish: true,
        players: [first, second],
        nextBefore: null,
        items: moments.map((caption, index) => {
          const player = index % 2 === 0 ? first : second;
          const id = `d8000000-0000-0000-0000-00000000010${String(index)}`;
          const detail = `${seasonPath}/gallery/${id}`;
          return {
            id,
            playerId: player.id,
            playerName: player.name,
            caption,
            createdAt,
            imageUrl: `${detail}/image`,
            thumbnailUrl: `${detail}/thumbnail`,
            reactions: [{ emoji: '🔥', count: index + 1, selected: index === 0 }],
          };
        }),
      } satisfies Schemas['GalleryView'],
    }),
  );
  await page.route(`**${seasonPath}/gallery/*/thumbnail`, (route) =>
    route.fulfill({ path: '../content/map.demo.background.png', contentType: 'image/png' }),
  );
  await page.goto(`/seasons/${seasonId}/gallery`);
  const pictures = page.locator('main img[src$="/thumbnail"]');
  await expect
    .poll(() =>
      pictures.evaluateAll(
        (images) =>
          images.filter(
            (img) => img instanceof HTMLImageElement && img.complete && img.naturalWidth > 0,
          ).length,
      ),
    )
    .toBe(moments.length);
  await expectScreen(page, 'Галерея моментов', 'gallery-grid.png');
});
