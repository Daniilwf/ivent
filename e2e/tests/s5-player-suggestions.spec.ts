import { fileURLToPath } from 'node:url';
import { expect, test, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, live, setUpSeason, signIn } from '../support/world.ts';

const shot = (name: string) =>
  fileURLToPath(new URL(`../../test-artifacts/design/${name}`, import.meta.url));

async function captureBox(page: Page, name: string) {
  await page.evaluate(() => document.fonts.ready);
  await page.getByTestId('player-suggestions').evaluate((section) => {
    section.scrollIntoView({ block: 'center', behavior: 'instant' });
  });
  await page.screenshot({ path: shot(name), scale: 'css' });
}

test('a player proposes a game and an item from the season and privately sees the moderation', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Автор предложений', 'Другой игрок'], start: false });
  const [author, other] = world.players;
  if (!author || !other) throw new Error('No players');
  const admin = world.adminApi;
  const path = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const rules = await admin.get<Schemas['RulesView']>(`${path}/rules`);
  await admin.put(`${adminPath}/rules`, {
    expectedVersion: rules.version,
    ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, suggestions: true } },
  });
  await admin.post(`${adminPath}/status`, { to: 'active' });
  const playerApi = await apiOf(author);
  const player = await signIn(browser, author);
  const another = await signIn(browser, other);
  try {
    const column = player.getByTestId('season-right');
    const box = player.getByTestId('player-suggestions');
    await expect(box).toBeVisible(live);
    await expect(
      box.getByText('Здесь появятся отправленные предложения и ответы админа.'),
    ).toBeVisible(live);
    await captureBox(player, `publish-stage5-player-suggestions-empty-${info.project.name}.png`);
    await box.getByText('Предложить игру, ивент или предмет', { exact: true }).click();
    const title = `Тихий космос ${world.seasonName}`;
    await box.getByRole('textbox', { name: 'Название игры', exact: true }).fill(title);
    await box
      .getByRole('textbox', { name: 'Категории через запятую', exact: true })
      .fill('Приключение');
    await captureBox(player, `publish-stage5-player-suggestions-form-${info.project.name}.png`);
    const submit = box.getByRole('button', { name: 'Отправить предложение', exact: true });
    await expect(submit).toBeInViewport({ ratio: 1 });
    expect(await column.evaluate((pane) => pane.scrollWidth - pane.clientWidth)).toBe(0);
    expect(
      await submit.evaluate((control) => {
        const rect = control.getBoundingClientRect();
        const target = document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2);
        return target === control || control.contains(target);
      }),
    ).toBe(true);
    if (info.project.name === 'desktop') {
      const viewport = player.viewportSize();
      if (!viewport) throw new Error('Expected a desktop viewport');
      await player.setViewportSize({ ...viewport, width: 900 });
      expect(await column.evaluate((pane) => pane.scrollWidth - pane.clientWidth)).toBe(0);
      await submit.scrollIntoViewIfNeeded();
      await expect(submit).toBeInViewport({ ratio: 1 });
      await player.setViewportSize(viewport);
    }
    await box.getByText('Дополнить карточку игры', { exact: true }).click();
    await box.getByRole('spinbutton', { name: 'Длина игры, часы', exact: true }).fill('8.5');
    await box.getByRole('textbox', { name: 'Описание предложения', exact: true }).click();
    await player.keyboard.press('Tab');
    await expect(submit).toBeFocused();
    await player.keyboard.press('Enter');
    await expect(box.getByText('Предложение отправлено', { exact: true })).toBeVisible(live);
    await expect(box.getByRole('combobox', { name: 'Что предлагаешь', exact: true })).toBeFocused();
    await expect(
      box.getByRole('combobox', { name: 'Что предлагаешь', exact: true }),
    ).toBeInViewport({ ratio: 1 });
    await expect(box.getByRole('heading', { name: title, exact: true })).toBeVisible(live);
    const game = (await playerApi.get<Schemas['SuggestionView'][]>(`${path}/suggestions`)).find(
      (item) => item.game?.title === title,
    );
    if (!game) throw new Error('The submitted game is missing');
    await admin.post(`${adminPath}/suggestions/${game.id}/review`, { approve: true });
    await expect(box.getByText('Одобрено', { exact: true })).toBeVisible(live);

    await box.getByRole('combobox', { name: 'Что предлагаешь', exact: true }).selectOption('item');
    const text = 'Фонарь, который подсвечивает ближайшую развилку.';
    await box.getByRole('textbox', { name: 'Описание предложения', exact: true }).fill(text);
    await box.getByRole('button', { name: 'Отправить предложение', exact: true }).click();
    await expect(box.getByRole('listitem').filter({ hasText: text })).toContainText(
      'На рассмотрении',
      live,
    );
    const item = (await playerApi.get<Schemas['SuggestionView'][]>(`${path}/suggestions`)).find(
      (entry) => entry.text === text,
    );
    if (!item) throw new Error('The submitted item is missing');
    const comment = 'Опиши, чем это поможет на клетке с двумя выходами.';
    await admin.post(`${adminPath}/suggestions/${item.id}/review`, { approve: false, comment });
    await expect(box.getByText(comment, { exact: true })).toBeVisible(live);
    await expect(box.getByText('Отклонено', { exact: true })).toBeVisible(live);
    await expect(another.getByTestId('player-suggestions')).toBeVisible(live);
    await expect(another.getByText(text, { exact: true })).toHaveCount(0);
    await expect(another.getByText(comment, { exact: true })).toHaveCount(0);
    await box.getByText('Предложить игру, ивент или предмет', { exact: true }).click();
    await captureBox(player, `publish-stage5-player-suggestions-${info.project.name}.png`);
    const longText = `${'Каждую неделю вместе выбираем необычное условие для следующей игры. '.repeat(14)}${'Непрерывныйтекст'.repeat(12)}`;
    await playerApi.post(`${path}/suggestions`, { kind: 'event', text: longText });
    await expect(box.getByRole('listitem').filter({ hasText: longText })).toContainText(
      'На рассмотрении',
      live,
    );
    await captureBox(player, `publish-stage5-player-suggestions-dense-${info.project.name}.png`);
    expect(await column.evaluate((pane) => pane.scrollWidth - pane.clientWidth)).toBe(0);
    expect(await player.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
  } finally {
    await player.context().close();
    await another.context().close();
    await playerApi.dispose();
    await admin.dispose();
  }
});

test('the player suggestion box follows its feature flag without a page reload', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Участник предложки'] });
  const [author] = world.players;
  if (!author) throw new Error('No player');
  const admin = world.adminApi;
  const path = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const player = await signIn(browser, author);
  try {
    await expect(player.getByTestId('turn')).toBeVisible(live);
    await expect(player.getByTestId('player-suggestions')).toHaveCount(0);
    const rules = await admin.get<Schemas['RulesView']>(`${path}/rules`);
    await admin.put(`${adminPath}/rules`, {
      expectedVersion: rules.version,
      ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, suggestions: true } },
    });
    await expect(player.getByTestId('player-suggestions')).toBeVisible(live);
    const enabled = await admin.get<Schemas['RulesView']>(`${path}/rules`);
    await admin.put(`${adminPath}/rules`, {
      expectedVersion: enabled.version,
      ruleset: {
        ...enabled.ruleset,
        features: { ...enabled.ruleset.features, suggestions: false },
      },
    });
    await expect(player.getByTestId('player-suggestions')).toHaveCount(0, live);
    await expect(player.getByTestId('turn')).toBeVisible();
  } finally {
    await player.context().close();
    await admin.dispose();
  }
});
