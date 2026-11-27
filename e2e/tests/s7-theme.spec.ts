import { expect, test, type Page } from '@playwright/test';
import { setUpSeason, signInHere } from '../support/world.ts';

// The theme (D-940): «Светлая», «Тёмная», «Как в системе» in my menu, kept on this device across a reload and applied
// before the first paint. Runs on the phone and the desktop projects alike.

/** The page's background colour as the browser paints it */
function pageColour(page: Page) {
  return page.evaluate(() => getComputedStyle(document.documentElement).backgroundColor);
}

async function pick(page: Page, choice: 'light' | 'dark' | 'system') {
  await page.getByTestId('user-menu').click();
  const item = page.getByTestId(`theme-${choice}`);
  await item.click();
  await expect(item).toHaveAttribute('aria-checked', 'true');
  await page.keyboard.press('Escape');
}

const firstPaintCases = [
  { saved: 'dark', system: 'light', bar: '#1a1c22', paint: 'rgb(26, 28, 34)' },
  { saved: 'light', system: 'dark', bar: '#eef2f7', paint: 'rgb(238, 242, 247)' },
  { saved: null, system: 'dark', bar: '#1a1c22', paint: 'rgb(26, 28, 34)' },
  { saved: null, system: 'light', bar: '#eef2f7', paint: 'rgb(238, 242, 247)' },
] as const;

for (const { saved, system, bar, paint } of firstPaintCases) {
  test(`the first paint follows ${saved ?? 'system'} on a ${system} device before the app loads`, async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme: system });
    await page.addInitScript((choice) => {
      if (choice) localStorage.setItem('ge.theme', choice);
    }, saved);

    let releaseAssets: (() => void) | undefined;
    const assets = new Promise<void>((resolve) => {
      releaseAssets = resolve;
    });
    const requested = new Set<string>();
    await page.route('**/assets/*.{css,js}', async (route) => {
      requested.add(route.request().resourceType());
      await assets;
      await route.continue();
    });

    try {
      await page.goto('/', { waitUntil: 'commit' });
      await expect.poll(() => [...requested].sort()).toEqual(['script', 'stylesheet']);
      await expect(page.locator('meta[name="color-scheme"]')).toHaveAttribute(
        'content',
        saved ?? 'light dark',
      );
      await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute('content', bar);
      expect(await pageColour(page)).toBe(paint);
      expect(
        await page.locator('html').evaluate((html) => getComputedStyle(html).colorScheme),
      ).toBe(saved ?? system);
      expect(
        await page.evaluate(() =>
          getComputedStyle(document.documentElement).getPropertyValue('--color-page'),
        ),
      ).toBe('');
    } finally {
      releaseAssets?.();
      await page.unrouteAll({ behavior: 'wait' });
    }
  });
}

test('the theme switches in my menu and stays after a reload', async ({ page }) => {
  const world = await setUpSeason({ players: ['Сова'] });
  const [player] = world.players;
  if (!player) throw new Error('No player.');
  await page.emulateMedia({ colorScheme: 'light' });
  await page.goto('/');
  await signInHere(page, player);

  // «Как в системе» is the default: a light system gives the light page
  const light = await pageColour(page);
  await expect(page.locator('html')).not.toHaveAttribute('data-theme');

  await pick(page, 'dark');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page.locator('meta[name="color-scheme"]')).toHaveAttribute('content', 'dark');
  const dark = await pageColour(page);
  expect(dark).not.toBe(light);
  await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute('content', '#1a1c22');

  // A reload keeps it
  await page.reload();
  await expect(page.getByTestId('user-menu')).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await pageColour(page)).toBe(dark);

  // «Как в системе» follows the system, also when it changes while the page is open
  await pick(page, 'system');
  await expect(page.locator('html')).not.toHaveAttribute('data-theme');
  await expect(page.locator('meta[name="color-scheme"]')).toHaveAttribute('content', 'light dark');
  expect(await pageColour(page)).toBe(light);
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect.poll(() => pageColour(page)).toBe(dark);
  await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute('content', '#1a1c22');

  // «Светлая» wins over a dark system, after a reload too
  await pick(page, 'light');
  await expect.poll(() => pageColour(page)).toBe(light);
  await page.reload();
  await expect(page.getByTestId('user-menu')).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await expect(page.locator('meta[name="color-scheme"]')).toHaveAttribute('content', 'light');
  expect(await pageColour(page)).toBe(light);
  await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute('content', '#eef2f7');
});

test('the account menu keeps every action reachable on a short viewport', async ({ page }) => {
  const world = await setUpSeason({ players: ['Сова'] });
  try {
    await page.setViewportSize({ width: 390, height: 360 });
    await page.goto('/');
    await signInHere(page, world.admin);
    const opener = page.getByTestId('user-menu');
    await opener.click();
    const menu = page.getByRole('menu');
    await expect.poll(() => menu.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);

    await page.keyboard.press('End');
    const logout = menu.getByTestId('logout');
    await expect(logout).toBeFocused();
    await expect(logout).toBeInViewport({ ratio: 0.9 });
    await expect(menu.getByTestId('change-password')).toBeInViewport({ ratio: 0.9 });
    await page.keyboard.press('Escape');
    await expect(opener).toBeFocused();
  } finally {
    await world.adminApi.dispose();
  }
});
