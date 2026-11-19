import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';

// Smoke tests (I3, D-128): against any address — the local site, the test copy, the live site — and read-only there:
// nothing here writes. `npm run test:smoke -- https://<address>`. A signed-in look runs only with an account given in
// SMOKE_LOGIN / SMOKE_PASSWORD (a spectator on the live site), so the live data is never touched.

test.describe('smoke @smoke', () => {
  test('the site is healthy', async ({ request }) => {
    const health = await request.get('/health');

    expect(health.status()).toBe(200);
  });

  test('the sign-in page opens without browser errors', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', (error) => errors.push(error.message));
    // The browser logs every non-2xx answer too: a stranger's 401 from /api/auth/me is how the page learns to show sign-in
    page.on('console', (message) => {
      if (message.type() === 'error' && !message.text().startsWith('Failed to load resource'))
        errors.push(message.text());
    });

    await page.goto('/');

    await expect(page.getByRole('heading', { name: 'Вход' })).toBeVisible();
    await expect(page.getByTestId('login-submit')).toBeEnabled();
    expect(errors).toEqual([]);
  });

  test('the API answers and keeps its doors shut to strangers', async ({ request }) => {
    const status = await request.get('/api/status');
    const seasons = await request.get('/api/seasons');
    const admin = await request.get('/api/admin/bug-reports');

    expect(status.status()).toBe(200);
    expect(typeof ((await status.json()) as { maintenance: unknown }).maintenance).toBe('boolean');
    expect(seasons.status()).toBe(401);
    expect(admin.status()).toBe(401);

    const unknownSeason = '/api/seasons/00000000-0000-0000-0000-000000000000';
    for (const section of ['polls', 'interactions']) {
      const view = await request.get(`${unknownSeason}/${section}`);
      expect(view.status(), section).toBe(401);
    }
  });

  test('pages carry the security headers', async ({ request }) => {
    const page = await request.get('/');
    const headers = page.headers();

    expect(page.status()).toBe(200);
    expect(headers['x-content-type-options']).toBe('nosniff');
    expect(headers['x-frame-options']).toBe('DENY');
    // Only the site's own scripts run (SEC-1, D-462)
    expect(headers['content-security-policy']).toContain("script-src 'self';");
    expect(headers['content-security-policy']).toContain("object-src 'none'");
  });

  test('a write without a session is refused', async ({ request }) => {
    const answer = await request.post('/api/bug-reports', {
      data: { commandId: crypto.randomUUID(), page: '/', text: 'smoke' },
    });

    // No session: refused before anything is read or written (401, or 400 for the missing CSRF token)
    expect([400, 401]).toContain(answer.status());
  });

  test('a spectator sees the current season and reads its polls and interactions', async ({
    page,
  }) => {
    const login = process.env['SMOKE_LOGIN'] ?? '';
    const password = process.env['SMOKE_PASSWORD'] ?? '';
    test.skip(!login || !password, 'SMOKE_LOGIN and SMOKE_PASSWORD are not set');

    await page.goto('/');
    await page.getByTestId('login-name').fill(login);
    await page.getByTestId('login-password').fill(password);
    await page.getByTestId('login-submit').click();

    await expect(page.getByTestId('current-user')).toBeVisible();
    const current = await page.request.get('/api/seasons/current');
    if (current.status() === 404) {
      await expect(page.getByTestId('no-season')).toBeVisible();
      return;
    }
    expect(current.ok()).toBe(true);
    const { id } = (await current.json()) as Schemas['CurrentSeasonView'];
    const response = await page.request.get(`/api/seasons/${id}`);
    expect(response.ok()).toBe(true);
    const { interactionFeatures, pollsEnabled } = (await response.json()) as Schemas['SeasonView'];

    await page.goto(`/seasons/${id}/polls`);
    const polls = page.getByRole('main');
    await expect(polls.getByRole('heading', { level: 1, name: 'Голосования' })).toBeVisible();
    if (pollsEnabled) {
      await expect(
        polls
          .getByRole('heading', {
            level: 2,
            name: /^(Пока нет голосований|Открытые|Завершённые)$/u,
          })
          .first(),
      ).toBeVisible();
    } else {
      await expect(polls.getByRole('heading', { name: 'Голосования выключены' })).toBeVisible();
    }

    await page.goto(`/seasons/${id}/interactions`);
    const interactions = page.getByRole('main');
    await expect(
      interactions.getByRole('heading', { level: 1, name: 'Взаимодействия' }),
    ).toBeVisible();
    if (interactionFeatures?.duels || interactionFeatures?.partnerBoard) {
      if (interactionFeatures.duels)
        await expect(interactions.getByRole('heading', { name: 'Дуэли' })).toBeVisible();
      if (interactionFeatures.partnerBoard)
        await expect(
          interactions.getByRole('heading', { name: 'Доска «ищу напарника»' }),
        ).toBeVisible();
    } else {
      await expect(
        interactions.getByRole('heading', { name: 'Взаимодействия недоступны в этом сезоне' }),
      ).toBeVisible();
    }
  });
});
