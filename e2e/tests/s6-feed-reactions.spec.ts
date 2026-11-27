import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import { commandId, type Schemas } from '../support/api.ts';
import { apiOf, live, rollAndStart, seasonOf, setUpSeason, signIn } from '../support/world.ts';

function reactionGroup(row: Locator) {
  return row.getByRole('group', { name: 'Реакции на запись' });
}

function fire(group: Locator) {
  return group.getByRole('button', { name: 'Реакция 🔥', exact: true });
}

function chooser(group: Locator) {
  return group.getByRole('button', { name: 'Добавить реакцию', exact: true });
}

async function showState(button: Locator, selected: boolean, count: string) {
  await expect(button).toHaveAttribute('aria-pressed', String(selected), live);
  await expect(button).toHaveAccessibleDescription(count, live);
}

async function capture(page: Page, info: TestInfo, state: 'compact' | 'choices' | 'error') {
  const viewport =
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };
  expect(page.viewportSize()).toEqual(viewport);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    viewport.width,
  );
  await page.evaluate(() => document.fonts.ready);
  const screenshot = info.outputPath(`feed-reactions-${state}-${info.project.name}.png`);
  await page.screenshot({ path: screenshot, scale: 'css' });
  await info.attach(`feed reactions ${state}`, { path: screenshot, contentType: 'image/png' });
}

test('a player and a spectator react live to an existing feed row until its command is undone', async ({
  browser,
}, info) => {
  const phone = info.project.name === 'phone';
  const world = await setUpSeason({ players: ['Вася', 'Катя', 'Коля'], spectator: true });
  const [player, ...others] = world.players;
  const spectator = world.spectator;
  if (!player || !spectator) throw new Error('A player and a spectator are required.');
  const playerApi = await apiOf(player);
  const seasonPath = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const rules = await world.adminApi.get<Schemas['RulesView']>(`${seasonPath}/rules`);
  const reactionOptions = rules.ruleset.social.reactions;
  if (!Array.isArray(reactionOptions)) throw new Error('The rules must publish the reaction list.');
  expect(rules.ruleset.features.reactions).toBe(false);

  for (const other of others) {
    const api = await apiOf(other);
    await rollAndStart(api, world.seasonId);
    await api.dispose();
  }
  await playerApi.post(`${seasonPath}/roll`);
  const offer = (await seasonOf(playerApi, world.seasonId)).me?.offer;
  if (!offer) throw new Error('Starting the run requires a saved offer.');
  const entryCommandId = commandId();
  await playerApi.post(`${seasonPath}/start`, { commandId: entryCommandId, gameId: offer.id });
  await playerApi.dispose();

  const [playerPage, spectatorPage] = await Promise.all([
    signIn(browser, player),
    signIn(browser, spectator),
  ]);
  // Use the season-specific feed so parallel scenarios can create other seasons.
  const path = `/seasons/${world.seasonId}/feed`;
  await Promise.all([playerPage.goto(path), spectatorPage.goto(path)]);
  const playerRow = playerPage.getByTestId(`feed-item-${entryCommandId}`);
  const spectatorRow = spectatorPage.getByTestId(`feed-item-${entryCommandId}`);
  const playerGroup = reactionGroup(playerRow);
  const spectatorGroup = reactionGroup(spectatorRow);
  for (const row of [playerRow, spectatorRow]) {
    await expect(row).toBeVisible();
    await expect(row).toContainText(player.name);
    await expect(reactionGroup(row)).toHaveCount(0);
  }

  await world.adminApi.put(`${adminPath}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: { ...rules.ruleset.features, reactions: true, comments: true },
    },
  });
  for (const group of [playerGroup, spectatorGroup]) {
    await expect(chooser(group)).toHaveAttribute('aria-expanded', 'false', live);
    await expect(group.getByRole('button')).toHaveCount(1);
    await expect(fire(group)).toHaveCount(0);
  }

  const playerChooser = chooser(playerGroup);
  const spectatorChooser = chooser(spectatorGroup);
  const playerFire = fire(playerGroup);
  const spectatorFire = fire(spectatorGroup);
  const playerComments = playerRow.getByRole('button', { name: /^Комментарии к записи/ });
  if (phone) {
    await playerChooser.tap();
    await expect(playerGroup.getByRole('button')).toHaveCount(reactionOptions.length + 1);
    await showState(playerFire, false, '0 реакций');
    await playerFire.tap();
  } else {
    await playerChooser.focus();
    await playerPage.keyboard.press('Tab');
    await expect(playerComments).toBeFocused();
    await playerPage.keyboard.press('Shift+Tab');
    await expect(playerChooser).toBeFocused();
    await playerPage.keyboard.press('Enter');
    await expect(playerGroup.getByRole('button')).toHaveCount(reactionOptions.length + 1);
    const first = String(reactionOptions[0]);
    await playerPage.keyboard.press('Tab');
    await expect(
      playerGroup.getByRole('button', { name: `Реакция ${first}`, exact: true }),
    ).toBeFocused();
    await playerPage.keyboard.press('Escape');
    await expect(playerChooser).toBeFocused();
    await expect(playerFire).toHaveCount(0);
    await playerPage.keyboard.press('Enter');
    for (const emoji of reactionOptions) {
      await playerPage.keyboard.press('Tab');
      if (emoji === '🔥') break;
    }
    await expect(playerFire).toBeFocused();
    await playerPage.keyboard.press('Enter');
  }
  await showState(playerFire, true, '1 реакция');
  await expect(playerChooser).toHaveAccessibleDescription('1 реакция', live);
  await expect(spectatorChooser).toHaveAccessibleDescription('1 реакция', live);
  await spectatorChooser.click();
  await showState(spectatorFire, false, '1 реакция');
  await expect(playerChooser).toHaveAttribute('aria-expanded', 'true');
  if (!phone) await expect(playerFire).toBeFocused();

  if (phone) await spectatorFire.tap();
  else await spectatorFire.click();
  await showState(spectatorFire, true, '2 реакции');
  await showState(playerFire, true, '2 реакции');

  await spectatorChooser.click();
  const comments = spectatorRow.getByRole('button', { name: /^Комментарии к записи/ });
  await expect(comments).toHaveAccessibleDescription('0 комментариев');
  const [commentBox, chooserBox, timeBox] = await Promise.all([
    comments.boundingBox(),
    spectatorChooser.boundingBox(),
    spectatorRow.locator('time').boundingBox(),
  ]);
  if (!commentBox || !chooserBox || !timeBox) throw new Error('The feed metadata must be visible.');
  expect(commentBox.y).toBe(chooserBox.y);
  expect(timeBox.y + timeBox.height / 2).toBeCloseTo(chooserBox.y + chooserBox.height / 2);
  for (const box of [commentBox, chooserBox]) {
    expect(box.width).toBeGreaterThanOrEqual(44);
    expect(box.height).toBeGreaterThanOrEqual(44);
  }
  expect(
    await spectatorPage.getByRole('group', { name: 'Реакции на запись' }).count(),
  ).toBeGreaterThan(4);
  await capture(spectatorPage, info, 'compact');

  if (phone) await spectatorChooser.tap();
  else {
    await spectatorChooser.focus();
    await spectatorPage.keyboard.press('Enter');
    await spectatorPage.keyboard.press('Tab');
  }
  await expect(spectatorChooser).toHaveAttribute('aria-expanded', 'true');
  await capture(spectatorPage, info, 'choices');

  await spectatorPage.route(
    `${seasonPath}/feed/${entryCommandId}/reactions`,
    async (route) => {
      await route.fulfill({ status: 409, json: { title: 'Test refusal' } });
    },
    { times: 1 },
  );
  const refused = spectatorGroup.getByRole('button', { name: 'Реакция 😂', exact: true });
  if (phone) await refused.tap();
  else await refused.click();
  await expect(spectatorRow).toContainText('Реакция не сохранилась. Попробуй ещё раз.');
  await showState(refused, false, '0 реакций');
  await capture(spectatorPage, info, 'error');

  if (phone) await playerFire.tap();
  else {
    await playerFire.focus();
    await playerPage.keyboard.press('Space');
  }
  await showState(playerFire, false, '1 реакция');
  await showState(spectatorFire, true, '1 реакция');

  await world.adminApi.post(`${adminPath}/undo`, {
    targetCommandId: entryCommandId,
    comment: 'Проверка отката записи с реакцией',
  });
  for (const row of [playerRow, spectatorRow]) {
    await expect(row).toContainText('Отменено', live);
    await expect(reactionGroup(row)).toHaveCount(0, live);
  }
  await world.adminApi.dispose();
});
