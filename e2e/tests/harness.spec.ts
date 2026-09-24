import { expect, test } from '@playwright/test';

// Proves the E2E harness works on both projects before the site exists (task A3).
test('browser renders a page in the configured viewport', async ({ page }, testInfo) => {
  await page.setContent('<main><h1>Игровой ивент</h1></main>');

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Игровой ивент');
  const expectedWidth = testInfo.project.name === 'phone' ? 390 : 1440;
  expect(page.viewportSize()?.width).toBe(expectedWidth);
});
