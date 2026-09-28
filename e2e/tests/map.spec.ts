import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  cellOf,
  completeHere,
  confirmButton,
  openAdmin,
  rollAndStartHere,
  setUpSeason,
  signIn,
} from '../support/world.ts';

// Stage 2 (2.10, 2.11, D-318): in a season of its own on the graph map, a completion walks the token to a fork, the
// player picks the branch that is not the default one with the keyboard and the token goes on along it; on a desktop
// the admin then changes the map in the editor and publishes it, and the player sees the change.

/** start → fork, then two branches of twelve cells (a… the default, b…) that meet at the finish */
function forkMap(): Schemas['MapGraphView'] {
  const cells: Schemas['CellView'][] = [
    { id: 'start', type: 'start', x: 0, y: 200 },
    { id: 'fork', type: 'fork', x: 120, y: 200 },
  ];
  const edges: Schemas['EdgeView'][] = [
    { from: 'start', to: 'fork', isDefaultForward: true, isPrimaryBackward: true },
  ];
  for (const [branch, y] of [
    ['a', 80],
    ['b', 320],
  ] as const) {
    let previous = 'fork';
    for (let i = 1; i <= 12; i++) {
      const id = `${branch}${String(i)}`;
      cells.push({ id, type: 'empty', x: 120 + i * 110, y });
      edges.push({
        from: previous,
        to: id,
        isDefaultForward: previous !== 'fork' || branch === 'a',
        isPrimaryBackward: true,
      });
      previous = id;
    }
    edges.push({
      from: previous,
      to: 'finish',
      isDefaultForward: true,
      isPrimaryBackward: branch === 'a',
    });
  }
  cells.push({ id: 'finish', type: 'finish', x: 1560, y: 200 });
  return { cells, edges, zones: [] };
}

test('a completion stops at the fork, the player picks a branch and the token walks it', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Картограф'], start: false });
  const [player] = world.players;
  if (!player) throw new Error('No player');
  const admin = world.adminApi;

  // The graph map, three dice a completion: a throw from the start always passes the fork
  const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
  const ruleset = {
    ...rules.ruleset,
    features: { ...rules.ruleset.features, mapMode: 'graph' as const },
    reward: {
      ...rules.ruleset.reward,
      diceCount: { ...rules.ruleset.reward.diceCount, min: 3, max: 3 },
    },
  };
  await admin.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset,
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/map/publish`, {
    map: forkMap(),
    comment: 'Карта с развилкой',
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/status`, { to: 'active' });

  // The player's only season opens by itself
  const page = await signIn(browser, player);
  await expect(page.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();
  await rollAndStartHere(page);
  await completeHere(page);

  // The token stands on the fork and the page asks for the branch: in the turn card on a phone, on the map on a desktop
  const branch = page.getByTestId('branch');
  await expect(branch).toBeVisible();
  if (testInfo.project.name.startsWith('phone'))
    await expect(page.getByTestId('turn').getByTestId('branch')).toBeVisible();
  else await expect(page.getByTestId('branch-hint')).toBeVisible();
  expect(await cellOf(page, player.name)).toBe('cell-fork');
  await expect(page.getByTestId('roll')).toHaveCount(0);

  // The branch that is not the default one, picked with the keyboard
  await branch.getByTestId('branch-b1').focus();
  await page.keyboard.press('Enter');

  await expect(branch).toHaveCount(0);
  await expect(page.getByTestId('roll')).toBeVisible();
  expect(await cellOf(page, player.name)).toMatch(/^cell-b\d+$/);

  // 2.11 on a desktop: the admin makes a5 a checkpoint in the editor and publishes it; the player sees it
  if (testInfo.project.name.startsWith('desktop')) {
    const editor = await signIn(browser, world.admin);
    await openAdmin(editor, world, 'map');
    await expect(editor.getByTestId('map-editor')).toBeVisible();
    await editor.getByTestId('map-cell-pick').selectOption('a5');
    await editor.getByTestId('map-cell-type').selectOption('checkpoint');
    await expect(editor.getByText('Ошибок нет: карту можно публиковать.')).toBeVisible();
    await editor.getByTestId('map-publish-comment').fill('Чекпоинт на верхней ветке');
    await editor.getByTestId('map-publish-button').click();
    await confirmButton(editor, 'Опубликовать').click();
    await expect(editor.getByText('Карта опубликована.')).toBeVisible();

    await page.reload();
    await expect(page.getByTestId('cell-a5')).toContainText('чекпоинт');
  }
});
