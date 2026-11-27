import { expect, test, type Locator } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { goToAdminSection, seasonOf, setUpSeason, signInHere } from '../support/world.ts';

async function expectHeadingBelowHeader(heading: Locator, header: Locator) {
  await expect(heading).toBeFocused();
  await expect(heading).toBeInViewport({ ratio: 1 });
  const [headerBox, headingBox] = await Promise.all([header.boundingBox(), heading.boundingBox()]);
  if (!headerBox || !headingBox) throw new Error('The heading or page header is missing.');
  expect(headingBox.y).toBeGreaterThanOrEqual(headerBox.y + headerBox.height);
}

test('an admin creates and opens a draft using an archived season', async ({ page }, info) => {
  const world = await setUpSeason({ players: ['Лис'] });
  const [player] = world.players;
  if (!player) throw new Error('The source needs a player.');

  const sourceId = crypto.randomUUID();
  const sourceName = `Архив походов по дальним играм с остановками у каждого маяка ${world.seasonName}`;
  const name = `Новый поход ${world.seasonName}`;
  const deadline = '2099-12-20T18:05';
  const archives = [
    { id: sourceId, name: sourceName },
    ...Array.from({ length: 8 }, (_, index) => ({
      id: crypto.randomUUID(),
      name: `Архив ${String(index + 1)}, ${world.seasonName}`,
    })),
  ];

  try {
    await Promise.all(
      archives.map(async ({ id, name: archiveName }) => {
        const adminSeason = `/api/admin/seasons/${id}`;
        await world.adminApi.post('/api/admin/seasons', { seasonId: id, name: archiveName });
        await world.adminApi.post(`${adminSeason}/players`, { userId: player.id });
        for (const to of ['active', 'closing', 'finished', 'archived'] as const) {
          await world.adminApi.post(`${adminSeason}/status`, { to });
        }
      }),
    );

    await page.goto('/');
    await signInHere(page, world.admin);
    await page.goto('/admin/season');

    const size =
      info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
    expect(page.viewportSize()).toEqual(size);
    const header = page.getByRole('banner');
    const shortcut = page.getByRole('button', { name: 'К созданию сезона' });
    const creationHeading = page.getByRole('heading', { name: 'Новый сезон', level: 2 });
    await expect(shortcut).toBeInViewport({ ratio: 1 });
    await expect(
      page.getByTestId('season-list').getByText(sourceName, { exact: true }),
    ).toBeVisible();
    await expect(creationHeading).not.toBeInViewport();
    await page.evaluate(() => document.fonts.ready);
    const shortcutPath = info.outputPath('season-create-entry.png');
    await page.screenshot({ path: shortcutPath, scale: 'css' });
    await info.attach('Shortcut to the season form', {
      path: shortcutPath,
      contentType: 'image/png',
    });

    if (info.project.name === 'phone') await shortcut.tap();
    else await shortcut.press('Enter');
    await expectHeadingBelowHeader(creationHeading, header);

    const source = page.getByRole('combobox', { name: 'Сезон по образцу' });
    await expect(source).toHaveValue('');
    await expect(source.getByRole('option', { name: world.seasonName, exact: true })).toHaveCount(
      0,
    );
    const nameField = page.getByTestId('season-name');
    await nameField.fill(name);
    await nameField.press('Tab');
    await expect(source).toBeFocused();
    await source.selectOption(sourceId);
    await expect(
      page.getByText(`В черновик перенесутся правила, карта и контент сезона «${sourceName}».`),
    ).toBeVisible();
    const deadlineField = page.getByTestId('season-template-deadline');
    await expect(deadlineField).toHaveValue('');
    await deadlineField.fill(deadline);

    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      size.width,
    );
    const action = page.getByTestId('season-create');
    const formPath = info.outputPath('clone-form.png');
    await page.screenshot({ path: formPath, scale: 'css' });
    await info.attach('Season template form', { path: formPath, contentType: 'image/png' });

    const [created] = await Promise.all([
      page.waitForResponse(
        (response) =>
          new URL(response.url()).pathname === '/api/admin/seasons' &&
          response.request().method() === 'POST',
      ),
      action.click(),
    ]);
    expect(created.ok(), await created.text()).toBe(true);
    const status = page.getByTestId('season-status');
    await expect(status).toContainText(name);
    await expect(status).toContainText('Готовится');
    const title = status.getByRole('heading', { level: 2 });
    await expectHeadingBelowHeader(title, header);
    await expect(status).toBeInViewport({ ratio: 1 });
    await expect(page.getByText(`Сезон создан: ${name}`, { exact: true })).toBeInViewport();
    await expect(page.getByTestId('season-deadline-panel')).toContainText('18:05 МСК');

    const seasons = await world.adminApi.get<Schemas['SeasonListItemView'][]>('/api/seasons');
    const target = seasons.find((season) => season.name === name);
    if (!target) throw new Error('The new season is not listed.');
    const draft = await seasonOf(world.adminApi, target.id);
    expect(draft.status).toBe('draft');
    expect(draft.players).toHaveLength(0);
    expect(Date.parse(draft.deadline ?? '')).toBe(Date.parse(`${deadline}:00+03:00`));
    const original = await seasonOf(world.adminApi, sourceId);
    expect(original.status).toBe('archived');
    expect(original.players).toHaveLength(1);

    const draftPath = info.outputPath('clone-created.png');
    await page.screenshot({ path: draftPath, scale: 'css' });
    await info.attach('New draft', { path: draftPath, contentType: 'image/png' });

    await goToAdminSection(page, 'log');
    const entry = page
      .getByTestId('admin-log')
      .getByRole('listitem')
      .filter({ hasText: 'Создание сезона по образцу' });
    await expect(entry).toHaveCount(1);
    await expect(entry.getByTestId('undo')).toHaveCount(0);
  } finally {
    await world.adminApi.dispose();
  }
});
