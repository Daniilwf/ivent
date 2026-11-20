import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';

const id = (value: number) => `fa600000-0000-0000-0000-${String(value).padStart(12, '0')}`;
const seasonName = 'Большой осенний сезон настольных приключений и длинных прохождений';

const seasons: Schemas['HallOfFameSeasonView'][] = [
  {
    seasonId: id(1),
    seasonName,
    winners: [
      { playerId: id(10), profileUserId: id(100), name: 'Лиса с Длинной Тропы', points: 1234 },
      { playerId: id(11), profileUserId: null, name: 'Филин', points: 1234 },
    ],
  },
  {
    seasonId: id(2),
    seasonName: 'Летний сезон',
    winners: [{ playerId: id(12), profileUserId: id(101), name: 'Енот', points: 248 }],
  },
];

const records: Schemas['HallOfFameRecordsView'] = {
  longestGames: [
    {
      seasonId: id(1),
      seasonName,
      gameId: id(20),
      runId: id(30),
      title: 'Долгая дорога домой',
      hours: 24.5,
    },
    {
      seasonId: id(2),
      seasonName: 'Летний сезон',
      gameId: id(21),
      runId: id(31),
      title: 'Путешествие через пустыню',
      hours: 24.5,
    },
  ],
  fastestFinishes: [],
};

test('the hall of fame shows long season names, tied winners and both longest games', async ({
  page,
}) => {
  await page.route('**/api/auth/antiforgery', (route) =>
    route.fulfill({ json: { headerName: 'x-csrf-token', token: 'visual' } }),
  );
  await page.route('**/api/auth/me', (route) =>
    route.fulfill({
      json: {
        id: id(200),
        login: 'spectator',
        name: 'Зритель',
        role: 'spectator',
        mustChangePassword: false,
        avatar: null,
      } satisfies Schemas['CurrentUser'],
    }),
  );
  await page.route('**/api/seasons/current', (route) => route.fulfill({ status: 404 }));
  await page.route('**/api/hall-of-fame', (route) => route.fulfill({ json: seasons }));
  await page.route('**/api/hall-of-fame/records', (route) => route.fulfill({ json: records }));

  await page.goto('/hall-of-fame');
  await expect(page.getByRole('heading', { name: 'Зал славы', level: 1 })).toBeInViewport();
  const archive = page.getByRole('region', { name: 'Победители сезонов' });
  const record = page.getByRole('region', { name: 'Самая длинная игра' });
  await expect(archive.getByRole('heading', { name: seasonName })).toBeVisible();
  await expect(archive.getByRole('link', { name: 'Лиса с Длинной Тропы' })).toBeVisible();
  await expect(archive.getByText('Филин', { exact: true })).toBeVisible();
  await expect(record.getByRole('link', { name: 'Долгая дорога домой' })).toBeVisible();
  await expect(record.getByRole('link', { name: 'Путешествие через пустыню' })).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    page.viewportSize()?.width ?? 0,
  );
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await expect(page).toHaveScreenshot('hall-of-fame.png', { fullPage: true });
});
