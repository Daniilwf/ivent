import { expect, test } from '@playwright/test';
import { commandId, type Schemas } from '../support/api.ts';
import { apiOf, live, setUpSeason, signIn } from '../support/world.ts';

test('a zero daily limit keeps earlier comments available without offering a new post', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [player] = world.players;
  if (!player) throw new Error('A player is required.');
  const actor = await apiOf(player);
  const seasonPath = `/api/seasons/${world.seasonId}`;
  const rollId = commandId();
  await actor.post(`${seasonPath}/roll`, { commandId: rollId });

  async function setLimit(commentsPerDay: number) {
    const rules = await world.adminApi.get<Schemas['RulesView']>(`${seasonPath}/rules`);
    await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
      expectedVersion: rules.version,
      ruleset: {
        ...rules.ruleset,
        features: { ...rules.ruleset.features, comments: true },
        social: { ...rules.ruleset.social, commentsPerDay },
      },
    });
  }

  await setLimit(2);
  const original = 'Давно ждал эту игру.';
  const commentId = commandId();
  await actor.post(`${seasonPath}/feed/${rollId}/comments`, {
    commandId: commentId,
    commentId,
    text: original,
  });
  const page = await signIn(browser, player);
  await page.goto(`/seasons/${world.seasonId}/feed`);
  const row = page.getByTestId(`feed-item-${rollId}`);
  await row.getByRole('button', { name: /^Комментарии к записи/ }).click();
  const discussion = row.getByRole('region', { name: /^Комментарии к записи/ });
  const composer = discussion.getByRole('textbox', { name: 'Написать комментарий' });
  await expect(composer).toBeVisible();

  await setLimit(0);
  await expect(
    discussion.getByText('Публикация новых комментариев отключена.', { exact: true }),
  ).toBeVisible(live);
  await expect(composer).toHaveCount(0);
  await expect(discussion.getByText(original, { exact: true })).toBeVisible();
  await expect(discussion.getByRole('button', { name: 'Изменить', exact: true })).toBeEnabled();

  await setLimit(2);
  await expect(composer).toBeVisible(live);
  await actor.dispose();
  await world.adminApi.dispose();
});
