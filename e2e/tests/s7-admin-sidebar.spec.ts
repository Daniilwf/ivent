import { expect, test } from '@playwright/test';
import { seedAdmin, type Schemas } from '../support/api.ts';
import { signInHere } from '../support/world.ts';

test('the desktop admin sidebar stays visible when the site header wraps while scrolled', async ({
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
    if (new URL(route.request().url()).searchParams.get('includeAdmin') === 'true') {
      await route.continue();
      return;
    }
    const response = await route.fetch();
    const navigation = (await response.json()) as Schemas['SeasonNavigationView'];
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
    const getBounds = () =>
      page.evaluate(() => {
        const header = document.querySelector('header:has([data-testid="site-nav"])');
        const proofs = document.querySelector('[data-testid="admin-nav-proofs"]');
        const sidebar = proofs?.closest('nav');
        if (!header || !proofs || !sidebar)
          throw new Error('The two navigation bars must be present.');
        return {
          headerBottom: header.getBoundingClientRect().bottom,
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
    await expect(header.getByTestId('nav-gallery')).toBeVisible();
    await expect(header.getByTestId('nav-polls')).toBeVisible();
    await expect
      .poll(async () => (await getBounds()).headerBottom)
      .toBeGreaterThan(compact.headerBottom);
    await proofs.focus();
    await expect(proofs).toBeFocused();
    await expect(title).toBeVisible();
    await expect
      .poll(async () => {
        const wrapped = await getBounds();
        return wrapped.sidebarTop - wrapped.headerBottom;
      })
      .toBeGreaterThanOrEqual(0);
    const wrapped = await getBounds();
    expect(wrapped.proofTop).toBeGreaterThanOrEqual(wrapped.headerBottom);
    expect(wrapped.sidebarBottom).toBeLessThanOrEqual(wrapped.viewportBottom);

    await testInfo.attach('sticky sidebar bounds', {
      body: JSON.stringify({ compact, wrapped }, null, 2),
      contentType: 'application/json',
    });
    await page.screenshot({ path: testInfo.outputPath('sidebar-below-wrapped-header.png') });
  } finally {
    revealNavigation();
  }
});
