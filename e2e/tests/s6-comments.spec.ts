import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import { PNG } from 'pngjs';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  commandsOf,
  complete,
  live,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

const feedLabel = /^Комментарии к записи/;
const reviewLabel = /^Комментарии к отзыву/;
type View = { page: Page; target: Locator };

function threadButton(target: Locator, name: RegExp) {
  return target.getByRole('button', { name });
}

async function openComments({ page, target }: View, name: RegExp, phone: boolean) {
  const toggle = threadButton(target, name);
  if (phone) await toggle.tap();
  else {
    await toggle.focus();
    await page.keyboard.press('Enter');
  }
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  const comments = target.getByRole('region', { name });
  await expect(comments.getByRole('textbox', { name: 'Написать комментарий' })).toBeVisible();
  return comments;
}

async function capture(page: Page, info: TestInfo, kind: 'feed' | 'review') {
  const viewport =
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
  expect(page.viewportSize()).toEqual(viewport);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    viewport.width,
  );
  await page.evaluate(() => document.fonts.ready);
  const screenshot = info.outputPath(`comments-${kind}-${info.project.name}.png`);
  const png = PNG.sync.read(await page.screenshot({ path: screenshot, scale: 'css' }));
  expect({ width: png.width, height: png.height }).toEqual(viewport);
  await info.attach(`${kind} comments`, { path: screenshot, contentType: 'image/png' });
}

async function checkComments(
  views: { author: View; reader: View; admin: View },
  options: {
    name: RegExp;
    apiPath: string;
    kind: 'feed' | 'review';
    original: string;
    edited: string;
  },
  info: TestInfo,
) {
  const { author, reader, admin } = views;
  const { name, apiPath, kind, original, edited } = options;
  const phone = info.project.name === 'phone';
  const ownThread = await openComments(author, name, phone);
  await expect(ownThread.getByText('Комментариев пока нет.', { exact: true })).toBeVisible();
  const composer = ownThread.getByRole('textbox', { name: 'Написать комментарий' });
  await composer.fill(original);
  const [published] = await Promise.all([
    author.page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === apiPath && response.request().method() === 'POST',
    ),
    ownThread.getByRole('button', { name: 'Отправить', exact: true }).click(),
  ]);
  expect(published.status()).toBe(204);
  await expect(composer).toHaveValue('');
  await expect(ownThread.getByText(original, { exact: true })).toBeVisible();

  const [readerThread, adminThread] = await Promise.all([
    openComments(reader, name, phone),
    openComments(admin, name, phone),
  ]);
  for (const thread of [readerThread, adminThread]) {
    await expect(thread.getByText(original, { exact: true })).toBeVisible();
    await expect(thread.getByRole('button', { name: 'Изменить', exact: true })).toHaveCount(0);
  }
  for (const thread of [ownThread, readerThread]) {
    await expect(thread.getByRole('button', { name: 'Скрыть', exact: true })).toHaveCount(0);
  }

  await ownThread.getByRole('button', { name: 'Изменить', exact: true }).click();
  const edit = ownThread.getByRole('textbox', { name: 'Изменить комментарий' });
  await expect(edit).toHaveValue(original);
  await edit.fill(edited);
  await ownThread.getByRole('button', { name: 'Сохранить', exact: true }).click();
  for (const thread of [ownThread, readerThread, adminThread]) {
    await expect(thread.getByText(edited, { exact: true })).toBeVisible(live);
    await expect(thread.getByText(original, { exact: true })).toHaveCount(0);
    await expect(thread.getByText('Изменён', { exact: true })).toBeVisible(live);
    await expect(thread.locator('em')).toHaveCount(0);
  }
  await ownThread.scrollIntoViewIfNeeded();
  await capture(author.page, info, kind);

  await adminThread.getByRole('button', { name: 'Скрыть', exact: true }).click();
  const confirm = admin.page.getByRole('alertdialog', { name: 'Скрыть комментарий?' });
  await confirm.getByRole('button', { name: 'Скрыть', exact: true }).click();
  await expect(confirm).toHaveCount(0);
  for (const thread of [ownThread, readerThread, adminThread]) {
    await expect(thread.getByText('Комментариев пока нет.', { exact: true })).toBeVisible(live);
    await expect(thread.getByText(edited, { exact: true })).toHaveCount(0);
  }

  const answer = await reader.page.request.get(apiPath);
  expect(answer.ok()).toBe(true);
  const body = (await answer.json()) as Schemas['CommentThreadView'];
  expect(body.comments).toEqual([]);
  expect(JSON.stringify(body)).not.toContain(original);
  expect(JSON.stringify(body)).not.toContain(edited);
}

test('a spectator edits comments on a feed entry and review before an admin hides them', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Вася'], spectator: true });
  const [player] = world.players;
  const spectator = world.spectator;
  if (!player || !spectator) throw new Error('A player and a spectator are required.');

  const playerApi = await apiOf(player);
  const reviewText = `Увлекательная игра с отличной концовкой. ${world.seasonName}`;
  const game = await rollAndStart(playerApi, world.seasonId);
  await complete(playerApi, world.seasonId, 'normal', { review: { rating: 9, text: reviewText } });
  const run = (await seasonOf(playerApi, world.seasonId)).me?.lastCompleted;
  const completion = (await commandsOf(world)).find(
    (command) => command.commandType === 'CompleteRun' && !command.undone,
  );
  if (!run || !completion) throw new Error('The completed run and its command are required.');
  await playerApi.dispose();

  const season = `/api/seasons/${world.seasonId}`;
  const rules = await world.adminApi.get<Schemas['RulesView']>(`${season}/rules`);
  expect(rules.ruleset.features.comments).toBe(false);
  const [author, reader, admin] = await Promise.all([
    signIn(browser, spectator),
    signIn(browser, player),
    signIn(browser, world.admin),
  ]);

  const feed = `/seasons/${world.seasonId}/feed`;
  const feedTarget = (page: Page) => ({
    page,
    target: page.getByTestId(`feed-item-${completion.commandId}`),
  });
  await author.goto(feed);
  await expect(feedTarget(author).target).toBeVisible();
  await expect(threadButton(feedTarget(author).target, feedLabel)).toHaveCount(0);
  await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, comments: true } },
  });
  await Promise.all([author.reload(), reader.goto(feed), admin.goto(feed)]);
  await checkComments(
    { author: feedTarget(author), reader: feedTarget(reader), admin: feedTarget(admin) },
    {
      name: feedLabel,
      apiPath: `${season}/feed/${completion.commandId}/comments`,
      kind: 'feed',
      original: 'Поздравляю с прохождением!',
      edited: 'Поздравляю! <em>Особенно понравился финал</em> & спасибо за запись.',
    },
    info,
  );

  const gamePath = `/games/${run.game.id}`;
  const reviewTarget = (page: Page) => ({
    page,
    target: page.getByTestId('game-runs').getByRole('listitem').filter({ hasText: reviewText }),
  });
  await Promise.all([author.goto(gamePath), reader.goto(gamePath), admin.goto(gamePath)]);
  await expect(author.getByRole('heading', { level: 1 })).toContainText(game);
  await checkComments(
    { author: reviewTarget(author), reader: reviewTarget(reader), admin: reviewTarget(admin) },
    {
      name: reviewLabel,
      apiPath: `/api/pool/${run.game.id}/runs/${run.id}/review-comments`,
      kind: 'review',
      original: 'Спасибо за отзыв, добавлю игру в список.',
      edited: 'Спасибо за отзыв! <em>Добавлю игру в список</em> & попробую на выходных.',
    },
    info,
  );
  await world.adminApi.dispose();
});
