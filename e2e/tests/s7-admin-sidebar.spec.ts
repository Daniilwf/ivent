import { expect, test, type Locator } from '@playwright/test';
import { seedAdmin, type Schemas } from '../support/api.ts';
import { signInHere } from '../support/world.ts';

test('the desktop admin sidebar stays visible when seasonal navigation becomes available', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'The phone uses a sheet instead of the sidebar.');

  await page.goto('/');
  await signInHere(page, seedAdmin);

  let revealNavigation!: () => void;
  const navigationReady = new Promise<void>((resolve) => {
    revealNavigation = resolve;
  });
  await page.route(/\/api\/seasons\/[^/]+\/navigation(?:\?|$)/, async (route) => {
    const response = await route.fetch();
    const navigation = (await response.json()) as Schemas['SeasonNavigationView'];
    if (new URL(route.request().url()).searchParams.get('includeAdmin') === 'true') {
      await route.fulfill({
        response,
        json: {
          ...navigation,
          adminFeatures: {
            ...navigation.adminFeatures,
            events: true,
            suggestions: true,
            globalEvents: true,
            weeklyChallenge: true,
          },
        },
      });
      return;
    }
    await navigationReady;
    await route.fulfill({
      response,
      json: { ...navigation, galleryEnabled: true, pollsEnabled: true },
    });
  });

  try {
    await page.goto('/admin/pool');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Пул и категории');
    await expect(page.getByTestId('games-count')).toBeVisible();
    const header = page.getByRole('banner');
    const sidebar = page.getByRole('navigation', { name: 'Разделы админки' });
    const title = sidebar.getByText('Админка', { exact: true });
    const proofs = sidebar.getByTestId('admin-nav-proofs');
    const players = sidebar.getByTestId('admin-nav-players');
    const getBounds = () =>
      page.evaluate(() => {
        const header = document.querySelector('header:has([data-testid="site-nav"])');
        const proofs = document.querySelector('[data-testid="admin-nav-proofs"]');
        const sidebar = proofs?.closest('nav');
        if (!header || !proofs || !sidebar)
          throw new Error('The two navigation bars must be present.');
        return {
          headerBottom: header.getBoundingClientRect().bottom,
          headerHeight: header.getBoundingClientRect().height,
          sidebarTop: sidebar.getBoundingClientRect().top,
          sidebarBottom: sidebar.getBoundingClientRect().bottom,
          proofTop: proofs.getBoundingClientRect().top,
          viewportBottom: window.innerHeight,
        };
      });

    await page.evaluate(() => {
      window.scrollTo(0, 750);
    });
    await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(750);
    const compact = await getBounds();
    expect(compact.sidebarTop).toBeGreaterThanOrEqual(compact.headerBottom);

    revealNavigation();
    const more = header.getByRole('button', { name: 'Ещё', exact: true });
    await expect(more).toBeVisible();
    expect((await getBounds()).headerHeight).toBe(compact.headerHeight);
    await players.focus();
    await page.keyboard.press('Shift+Tab');
    await expect(proofs).toBeFocused();
    await expect(title).toBeVisible();
    await expect
      .poll(async () => {
        const ready = await getBounds();
        return ready.sidebarTop - ready.headerBottom;
      })
      .toBeGreaterThanOrEqual(0);
    const ready = await getBounds();
    expect(ready.proofTop).toBeGreaterThanOrEqual(ready.headerBottom);
    expect(ready.sidebarBottom).toBeLessThanOrEqual(ready.viewportBottom);

    const measureOutline = async (focusedLink: Locator) => {
      const outline = await focusedLink.evaluate((link) => {
        const nav = link.closest('nav');
        if (!nav) throw new Error('The focused link must belong to the sidebar.');
        const bounds = link.getBoundingClientRect();
        const clip = nav.getBoundingClientRect();
        const style = getComputedStyle(link);
        const width = Number.parseFloat(style.outlineWidth);
        const outset = width + Number.parseFloat(style.outlineOffset);
        const left = clip.left + nav.clientLeft;
        const top = clip.top + nav.clientTop;
        return {
          focused: link.matches(':focus-visible'),
          width,
          outset,
          clearance: {
            left: bounds.left - outset - left,
            right: left + nav.clientWidth - bounds.right - outset,
            top: bounds.top - outset - top,
            bottom: top + nav.clientHeight - bounds.bottom - outset,
          },
        };
      });
      expect(outline.focused).toBe(true);
      expect(outline.width).toBeGreaterThan(0);
      for (const [edge, clearance] of Object.entries(outline.clearance)) {
        expect(
          clearance,
          `The scrollport leaves room for the ${edge} outline`,
        ).toBeGreaterThanOrEqual(0);
      }
      return outline;
    };

    const firstOutline = await measureOutline(proofs);
    await page.screenshot({ path: testInfo.outputPath('sidebar-below-header.png') });

    const testTools = sidebar.getByTestId('admin-nav-test');
    await expect(testTools).toBeAttached();
    await expect(sidebar.getByTestId('admin-nav-weeklychallenges')).toBeAttached();
    await sidebar.getByTestId('admin-nav-site').focus();
    await page.keyboard.press('Tab');
    await expect(testTools).toBeFocused();
    await expect.poll(() => sidebar.evaluate((nav) => nav.scrollTop)).toBeGreaterThan(0);
    const lastOutline = await measureOutline(testTools);
    await page.screenshot({ path: testInfo.outputPath('sidebar-bottom-focus.png') });

    await more.click();
    await expect(page.getByTestId('nav-gallery')).toBeVisible();
    await expect(page.getByTestId('nav-polls')).toBeVisible();
    await page.keyboard.press('Escape');

    await testInfo.attach('sticky sidebar bounds', {
      body: JSON.stringify({ compact, ready, firstOutline, lastOutline }, null, 2),
      contentType: 'application/json',
    });
  } finally {
    revealNavigation();
  }
});
