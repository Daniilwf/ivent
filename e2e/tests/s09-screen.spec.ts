import { expect, test, type Page } from '@playwright/test';
import { rollAndStartHere, setUpSeason, signIn } from '../support/world.ts';

// TESTING.md scenario 9 «Экран телефона 390×844: главная, прохождение, карта с зумом». The phone project plays it at
// 390×844 with touch — the map zooms with two fingers; the desktop project checks the same screens at 1440×900 with a
// mouse wheel. Both: no sideways scroll, the one main action in view, the map zooms with its buttons and pans by drag.

/** The map's visible width in map units: smaller is closer */
async function mapWidth(page: Page): Promise<number> {
  const box = await page.locator('svg[role="application"]').first().getAttribute('viewBox');
  return Number(box?.split(' ')[2]);
}

async function mapCentre(page: Page): Promise<number> {
  const box = (await page.locator('svg[role="application"]').first().getAttribute('viewBox')) ?? '';
  const [x = 0, , w = 0] = box.split(' ').map(Number);
  return x + w / 2;
}

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
  }

  // The run: the game on top, the completion form under it, all of it reachable without sideways scroll
  const game = await rollAndStartHere(page);
  await expect(page.getByTestId('active-run')).toContainText(game);
  await expect(page.getByTestId('active-run')).toBeInViewport();
  const submit = page.getByTestId('complete-submit');
  await submit.scrollIntoViewIfNeeded();
  await expect(submit).toBeInViewport();
  const box = await submit.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
  await noSidewaysScroll(page);

  // The map: the buttons zoom
  const map = page.locator('svg[role="application"]').first();
  await map.scrollIntoViewIfNeeded();
  const start = await mapWidth(page);
  await page.getByRole('button', { name: 'Приблизить' }).click();
  await expect.poll(() => mapWidth(page)).toBeCloseTo(start * 0.8, 0);
  await page.getByRole('button', { name: 'Отдалить' }).click();
  await expect.poll(() => mapWidth(page)).toBeCloseTo(start, 0);

  const frame = await map.boundingBox();
  if (!frame) throw new Error('The map has no box.');
  const cx = frame.x + frame.width / 2;
  const cy = frame.y + frame.height / 2;
  if (phone) {
    // Two fingers apart: closer
    const cdp = await page.context().newCDPSession(page);
    const touch = (spread: number) => [
      { x: cx - spread, y: cy, id: 1 },
      { x: cx + spread, y: cy, id: 2 },
    ];
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: touch(20) });
    for (const spread of [40, 60, 80])
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: touch(spread) });
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  } else {
    // The mouse wheel: closer
    await page.mouse.move(cx, cy);
    await page.mouse.wheel(0, -300);
  }
  await expect.poll(() => mapWidth(page)).toBeLessThan(start * 0.9);

  // A drag moves the map
  const centre = await mapCentre(page);
  if (phone) {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Input.dispatchTouchEvent', {
      type: 'touchStart',
      touchPoints: [{ x: cx, y: cy, id: 3 }],
    });
    await cdp.send('Input.dispatchTouchEvent', {
      type: 'touchMove',
      touchPoints: [{ x: cx - 80, y: cy, id: 3 }],
    });
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  } else {
    await page.mouse.move(cx, cy);
    await page.mouse.down();
    await page.mouse.move(cx - 80, cy, { steps: 4 });
    await page.mouse.up();
  }
  await expect.poll(() => mapCentre(page)).toBeGreaterThan(centre);
  await noSidewaysScroll(page);
});
