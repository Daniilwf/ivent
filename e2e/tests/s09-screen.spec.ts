import { readFileSync } from 'node:fs';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { fingerDrag, mapView, pinchOut } from '../support/touch.ts';
import {
  apiOf,
  fillCompletionHere,
  live,
  rollAndStartHere,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

// TESTING.md scenario 9 «Экран телефона 390×844: главная, прохождение, карта с зумом». The phone project plays it at
// 390×844 with touch — the map zooms with two fingers; the desktop project checks the same screens at 1440×900 with a
// mouse wheel. Both: no sideways scroll, the one main action in view, the map zooms with its buttons and pans by drag.

const mapWidth = async (surface: Page | Locator) => (await mapView(surface)).width;
const mapCentre = async (surface: Page | Locator) => (await mapView(surface)).centre;

async function noSidewaysScroll(page: Page) {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
}

test('screen: the main page, the run and the map zoom on this device', async ({
  browser,
}, testInfo) => {
  const phone = testInfo.project.name === 'phone';
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');
  const page = await signIn(browser, vasya);
  expect(page.viewportSize()).toEqual(
    phone ? { width: 390, height: 844 } : { width: 1440, height: 900 },
  );

  // The main page: the season's name and the one main action in view, nothing wider than the screen
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(world.seasonName);
  await expect(page.getByTestId('roll')).toBeInViewport();
  await noSidewaysScroll(page);
  if (phone) {
    // The leaderboard waits in a sheet at the bottom of a phone
    await page.getByRole('button', { name: /Лидерборд/ }).click();
    const sheet = page.getByRole('dialog', { name: 'Лидерборд' });
    await expect(sheet).toContainText('Вася');
    await expect(sheet).toContainText('Петя');
    await page.keyboard.press('Escape');
    await expect(sheet).toHaveCount(0);
  } else {
    await expect(page.getByTestId('leaderboard')).toBeVisible();
    await page.getByRole('button', { name: 'Вся таблица' }).click();
    const table = page.getByRole('dialog', { name: 'Лидерборд' });
    await expect(table).toContainText('Вася');
    await expect(table).toContainText('Петя');
    await page.keyboard.press('Escape');
    await expect(table).toHaveCount(0);
    const scroll = await page.evaluate(() => document.documentElement.scrollHeight - innerHeight);
    expect(scroll).toBeLessThanOrEqual(1);
  }

  // The run: the game and its primary action on top; the form opens in a dialog and returns focus on close.
  const game = await rollAndStartHere(page);
  await expect(page.getByTestId('active-run')).toContainText(game);
  await expect(page.getByTestId('active-run')).toBeInViewport();
  const opener = page.getByTestId('complete-open');
  await expect(opener).toBeInViewport();
  const box = await opener.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
  await expect(page.getByTestId('complete-form')).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath('season.png'), scale: 'css' });
  await opener.click();
  await expect(page.getByTestId('complete-dialog').getByTestId('complete-form')).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('completion.png'), scale: 'css' });
  await page.keyboard.press('Escape');
  await expect(opener).toBeFocused();
  await noSidewaysScroll(page);

  // The map: the phone opens the compact preview before zooming.
  if (phone) {
    const preview = page.getByRole('application');
    expect((await preview.boundingBox())?.height).toBeLessThanOrEqual(220);
    await page.getByTestId('map-open').click();
    await expect(page.getByRole('dialog').getByRole('application')).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('full-map.png'), scale: 'css' });
  }
  const mapSurface = phone ? page.getByRole('dialog') : page;
  const map = mapSurface.getByRole('application');
  await map.scrollIntoViewIfNeeded();
  const start = await mapWidth(mapSurface);
  await mapSurface.getByRole('button', { name: 'Приблизить' }).click();
  await expect.poll(() => mapWidth(mapSurface)).toBeCloseTo(start * 0.8, 0);
  await mapSurface.getByRole('button', { name: 'Отдалить' }).click();
  await expect.poll(() => mapWidth(mapSurface)).toBeCloseTo(start, 0);

  const frame = await map.boundingBox();
  if (!frame) throw new Error('The map has no box.');
  const cx = frame.x + frame.width / 2;
  const cy = frame.y + frame.height / 2;
  if (phone) {
    // Two fingers apart: closer
    await pinchOut(page, { x: cx, y: cy });
  } else {
    // The mouse wheel: closer
    await page.mouse.move(cx, cy);
    await page.mouse.wheel(0, -300);
  }
  await expect.poll(() => mapWidth(mapSurface)).toBeLessThan(start * 0.9);

  // A drag moves the map once the board is wider than the frame: a view wider than the board stays centred (D-623).
  // A phone opens already closer than the whole board; a desktop's whole board needs more zoom first.
  if (!phone) {
    for (let i = 0; i < 3; i++)
      await mapSurface.getByRole('button', { name: 'Приблизить' }).click();
    await expect.poll(() => mapWidth(mapSurface)).toBeLessThan(start * 0.5);
  }
  const centre = await mapCentre(mapSurface);
  if (phone) {
    await fingerDrag(page, { x: cx, y: cy }, { x: -80, y: 0 });
  } else {
    await page.mouse.move(cx, cy);
    await page.mouse.down();
    await page.mouse.move(cx - 80, cy, { steps: 4 });
    await page.mouse.up();
  }
  await expect.poll(() => mapCentre(mapSurface)).toBeGreaterThan(centre);
  await noSidewaysScroll(page);
});

test('screen: the rating screenshot is ready for the player to send as proof', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Вася', 'Петя'] });
  const [player] = world.players;
  if (!player) throw new Error('No player.');
  const page = await signIn(browser, player);
  const api = await apiOf(player);
  await rollAndStartHere(page);
  await fillCompletionHere(page);

  const form = page.getByTestId('complete-form');
  await form.getByText('Отзыв и скрин — по желанию').click();
  await form.getByLabel('Оценка игры').selectOption('7');
  await form.getByLabel('Добавить скрин для пруфа').setInputFiles({
    name: 'credits.png',
    mimeType: 'image/png',
    buffer: readFileSync(new URL('../../content/map.demo.background.png', import.meta.url)),
  });
  const preview = form.getByRole('img', { name: 'Скрин 1' });
  await expect(preview).toBeVisible(live);
  const thumbnail = await preview.getAttribute('src');
  if (!thumbnail) throw new Error('The file has no thumbnail.');
  await preview.scrollIntoViewIfNeeded();
  await noSidewaysScroll(page);
  await page.screenshot({
    path: testInfo.outputPath('completion-with-screenshot.png'),
    scale: 'css',
  });

  await form.getByTestId('complete-submit').click();
  const proof = page.getByTestId('proof-details');
  const summary = proof.locator(':scope > summary');
  await expect(summary).toContainText('Скрин для пруфа готов', live);
  const run = (await seasonOf(api, world.seasonId)).me?.lastCompleted;
  expect(run?.review?.rating).toBe(7);
  expect(run?.proof ?? null).toBeNull();

  await summary.click();
  const shot = proof.getByRole('img', { name: 'Скрин 1' });
  const submit = proof.getByRole('button', { name: 'Отправить пруф' });
  await expect(shot).toBeVisible();
  await expect(shot).toHaveAttribute('src', thumbnail);
  await expect(shot).toBeInViewport({ ratio: 1 });
  await expect(submit).toBeInViewport({ ratio: 1 });
  await expect(proof.getByText('Скрина достаточно, чтобы отправить пруф.')).toBeVisible();
  await expect(proof.getByTestId('proof-link')).toBeHidden();
  await noSidewaysScroll(page);
  await page.screenshot({ path: testInfo.outputPath('prepared-proof.png'), scale: 'css' });

  await submit.click();
  await expect(proof.getByTestId('proof-status')).toContainText(
    'Пруф ждёт проверки админом.',
    live,
  );
  await expect(proof.getByTestId('proof-files').getByRole('link')).toHaveCount(1);
});
