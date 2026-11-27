import { expect, test } from '@playwright/test';
import { createAccount, openAdmin, setUpSeason, signInHere, uniqueTag } from '../support/world.ts';

test('the add-player form keeps a visible focus while accounts load', async ({
  page,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Игрок'] });
  let releaseAccounts: (() => void) | undefined;
  const accountsCanLoad = new Promise<void>((resolve) => {
    releaseAccounts = resolve;
  });

  try {
    const available = await createAccount(
      world.adminApi,
      'player',
      `available-${uniqueTag()}`,
      'Участник с длинным отображаемым именем для сезонного списка',
    );
    await page.goto('/');
    await signInHere(page, world.admin);
    await openAdmin(page, world, 'players');
    await page.route('**/api/admin/accounts', async (route) => {
      await accountsCanLoad;
      await route.continue();
    });

    await page.getByTestId('add-player-open').press('Enter');
    const heading = page.getByRole('heading', { name: 'Новый игрок в сезоне' });
    const account = page.getByTestId('add-player-account');
    await expect(account).toHaveCount(0);
    await expect(heading).toBeFocused();
    await expect(heading).toBeInViewport({ ratio: 1 });
    const header = await page.locator('header.sticky').boundingBox();
    const title = await heading.boundingBox();
    if (!title || !header)
      throw new Error('The section heading and the sticky header must be visible.');
    expect(title.y).toBeGreaterThanOrEqual(header.y + header.height);

    releaseAccounts?.();
    await expect(account).toBeVisible();
    await expect(heading).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(account).toBeFocused();

    await account.selectOption(available.id);
    const identity = `${available.name} (${available.login})`;
    await expect(account).toHaveAccessibleDescription(identity);
    const selectedIdentity = page
      .getByTestId('add-player-form')
      .getByText(identity, { exact: true })
      .filter({ visible: true });
    await expect(selectedIdentity).toBeInViewport({ ratio: 1 });
    const hasHorizontalScroll = await page.evaluate(
      () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
    );
    expect(hasHorizontalScroll).toBe(false);
    await page.screenshot({ path: testInfo.outputPath('selected-account-identity.png') });
  } finally {
    releaseAccounts?.();
    await page.unrouteAll({ behavior: 'wait' });
    await world.adminApi.dispose();
  }
});
