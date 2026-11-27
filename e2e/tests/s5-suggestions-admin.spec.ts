import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, live, openAdmin, setUpSeason, signIn } from '../support/world.ts';

const shot = (name: string) =>
  fileURLToPath(new URL(`../../test-artifacts/design/${name}`, import.meta.url));

test('an admin rejects an item idea with a comment and approves a duplicate game with a pool warning', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Автор предложений'], start: false });
  const [author] = world.players;
  if (!author) throw new Error('No author');
  const admin = world.adminApi;
  const player = await apiOf(author);
  const moderation = await signIn(browser, world.admin);
  const season = `/api/seasons/${world.seasonId}`;
  const adminSeason = `/api/admin/seasons/${world.seasonId}`;
  const suggestions = `${season}/suggestions`;

  try {
    const rules = await admin.get<Schemas['RulesView']>(`${season}/rules`);
    await admin.put(`${adminSeason}/rules`, {
      expectedVersion: rules.version,
      ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, suggestions: true } },
    });
    await admin.post(`${adminSeason}/status`, { to: 'active' });

    const title = `Город моховых часов ${world.seasonName}`;
    const game: Schemas['GameCard'] = {
      title,
      tags: ['Инди'],
      hours: 8,
      year: 2024,
      steamAppId: null,
      coverFileId: null,
      note: 'Подходит на один вечер с напарником.',
      isCoop: true,
      completionCondition: 'Дойти до общих титров.',
    };
    await admin.post('/api/pool', { ...game, force: true });
    await player.post(suggestions, {
      kind: 'game',
      text: 'Понравилась совместная кампания.',
      game,
    });
    const ideaText = 'Пусть свиток возвращает на предыдущую развилку.';
    await player.post(suggestions, { kind: 'item', text: ideaText });

    await openAdmin(moderation, world, 'suggestions');
    const list = moderation.getByTestId('suggestions-list');
    const gameCard = list
      .getByRole('article')
      .filter({ has: moderation.getByRole('heading', { name: title }) });
    const itemCard = list.getByRole('article').filter({ hasText: ideaText });
    await expect(gameCard).toContainText(author.name);
    await expect(gameCard).toContainText('Дойти до общих титров.');
    await expect(itemCard).toBeVisible();
    await moderation.evaluate(() => document.fonts.ready);
    await moderation.screenshot({ path: shot(`s5-23-admin-suggestions-${info.project.name}.png`) });

    const comment = 'Нужно описать, что произойдёт с оставшимися шагами.';
    await itemCard.getByRole('button', { name: 'Отклонить', exact: true }).click();
    const reason = moderation.getByRole('alertdialog');
    await reason.getByRole('textbox', { name: 'Комментарий' }).fill(comment);
    await reason.getByRole('button', { name: 'Отклонить', exact: true }).click();
    await expect(itemCard).toHaveCount(0, live);

    await gameCard.getByRole('button', { name: 'Одобрить', exact: true }).click();
    await expect(list.getByText('Предложение одобрено', { exact: true })).toBeVisible(live);
    await expect(list.getByText('Игра уже есть в пуле.', { exact: true })).toBeVisible(live);
    await expect(list.getByText('Нет предложений на рассмотрении', { exact: true })).toBeVisible(
      live,
    );
    await moderation.screenshot({
      path: shot(`s5-23-admin-suggestions-warning-${info.project.name}.png`),
    });

    await list.getByRole('combobox', { name: 'Статус' }).selectOption('all');
    await expect(gameCard).toContainText('Одобрено');
    await expect(itemCard).toContainText('Отклонено');
    await expect(itemCard).toContainText(comment);
    const playerView = await player.get<Schemas['SuggestionView'][]>(suggestions);
    expect(playerView.find((entry) => entry.game?.title === title)?.status).toBe('approved');
    expect(playerView.find((entry) => entry.text === ideaText)).toMatchObject({
      status: 'rejected',
      comment,
    });
    const pool = await admin.get<Schemas['PoolGameView'][]>(
      `/api/pool?query=${encodeURIComponent(title)}`,
    );
    expect(pool.filter((entry) => entry.title === title)).toHaveLength(1);
  } finally {
    await moderation.context().close();
    await player.dispose();
    await admin.dispose();
  }
});
