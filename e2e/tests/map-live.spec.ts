import { expect, test, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  cellOf,
  completeHere,
  confirmButton,
  live,
  openAdmin,
  rollAndStartHere,
  setUpSeason,
  signIn,
} from '../support/world.ts';
import { fingerDrag, mapMiddle, mapView, pinchOut } from '../support/touch.ts';

// Stage 2 closing (2.12, D-272): two browsers on the graph map. Player A reaches a fork and picks the branch that is not
// the default; player B, on the season's screen all along, sees A's token stop at the fork, then stand on the far end
// of the chosen branch — where only its teleport leads — and the feed tells the branch and the flight, all without a
// reload. On a desktop the admin then publishes the map mid-season while the players stand on it: refused while A
// chooses the branch and when the draft removes the cell A stands on, accepted for a valid change, which both players
// see live. On a phone B's graph map zooms with two fingers and pans with one (MP29, D-275).

/**
 * start → fork, the default branch a1…a6 and the branch b1…b10, both to the finish. b1, b2 and b3 are teleports to b9:
 * two dice of two sides (2–4 steps) always pass the fork and stop on one of them after the choice of b, and b9 lies
 * beyond the reach of a walk.
 */
function teleportMap(): Schemas['MapGraphView'] {
  const cells: Schemas['CellView'][] = [
    { id: 'start', type: 'start', x: 0, y: 200 },
    { id: 'fork', type: 'fork', x: 120, y: 200 },
  ];
  const edges: Schemas['EdgeView'][] = [
    { from: 'start', to: 'fork', isDefaultForward: true, isPrimaryBackward: true },
  ];
  for (const [branch, y, length] of [
    ['a', 80, 6],
    ['b', 320, 10],
  ] as const) {
    let previous = 'fork';
    for (let i = 1; i <= length; i++) {
      const id = `${branch}${String(i)}`;
      const teleport = branch === 'b' && i <= 3;
      cells.push(
        teleport
          ? { id, type: 'teleport', to: 'b9', x: 120 + i * 110, y }
          : { id, type: 'empty', x: 120 + i * 110, y },
      );
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
  cells.push({ id: 'finish', type: 'finish', x: 1400, y: 200 });
  return { cells, edges, zones: [] };
}

/** Marks the page: the mark survives anything but a reload */
async function markPage(page: Page) {
  await page.evaluate(() => {
    (window as unknown as { notReloaded: boolean }).notReloaded = true;
  });
}

async function wasNotReloaded(page: Page) {
  return page.evaluate(() => (window as unknown as { notReloaded?: boolean }).notReloaded === true);
}

test('a fork and a teleport seen live from another browser; the map published mid-season', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Путник', 'Наблюдатель'], start: false });
  const [walker, watcher] = world.players;
  if (!walker || !watcher) throw new Error('No players');
  const admin = world.adminApi;

  const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
  const ruleset = {
    ...rules.ruleset,
    features: { ...rules.ruleset.features, mapMode: 'graph' as const },
    reward: {
      ...rules.ruleset.reward,
      diceCount: { ...rules.ruleset.reward.diceCount, min: 2, max: 2 },
      dieByDifficulty: { ...rules.ruleset.reward.dieByDifficulty, normal: { sides: 2 } },
    },
  };
  await admin.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset,
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/map/publish`, {
    map: teleportMap(),
    comment: 'Карта с телепортами',
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/status`, { to: 'active' });

  // Both open the season; B's page is marked, so a reload would show
  const a = await signIn(browser, walker);
  const b = await signIn(browser, watcher);
  for (const page of [a, b])
    await expect(page.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();
  await markPage(b);
  expect(await cellOf(b, walker.name)).toBe('cell-start');

  // A completes a game: the move stops at the fork and waits for the branch — B sees A's token on the fork
  await rollAndStartHere(a);
  await completeHere(a);
  await expect(a.getByTestId('branch')).toBeVisible();
  await expect.poll(() => cellOf(b, walker.name), live).toBe('cell-fork');

  const desktop = testInfo.project.name.startsWith('desktop');
  const editor = desktop ? await signIn(browser, world.admin) : null;
  if (editor) {
    // Refused: while a player chooses a branch the map cannot change under the paused move
    await openAdmin(editor, world, 'map');
    await expect(editor.getByTestId('map-editor')).toBeVisible();
    await editor.getByTestId('map-cell-pick').selectOption('a5');
    await editor.getByTestId('map-cell-type').selectOption('checkpoint');
    await expect(editor.getByTestId('map-problems')).toContainText(
      'Кто-то сейчас выбирает ветку на развилке',
    );
    await expect(editor.getByTestId('map-problems')).toContainText(walker.name);
    await expect(editor.getByTestId('map-publish-button')).toBeDisabled();
    await editor.getByTestId('map-reset').click();
  }

  // A picks the branch b; the token walks it, stops on a teleport and flies to b9
  await a.getByTestId('branch').getByTestId('branch-b1').click();
  await expect(a.getByTestId('branch')).toHaveCount(0);
  await expect.poll(() => cellOf(a, walker.name), live).toBe('cell-b9');

  // B sees it without a reload: A's token on b9, and the feed (opened from the menu) tells the branch and the flight
  await expect.poll(() => cellOf(b, walker.name), live).toBe('cell-b9');
  expect(await wasNotReloaded(b)).toBe(true);
  await b.getByTestId('nav-feed').first().click();
  const feed = b.getByTestId('feed');
  await expect(feed).toContainText(`${walker.name} выбирает ветку на развилке`);
  await expect(feed).toContainText('Телепорт');
  expect(await wasNotReloaded(b)).toBe(true);
  await b.goBack();
  await expect(b.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();

  if (!desktop) {
    // The graph on a phone, cropped around B's token (D-320 (9)): two fingers bring it closer, one finger drags it
    await expect(b.getByTestId('cell-b9')).toBeAttached();
    const middle = await mapMiddle(b);
    const start = await mapView(b);
    await pinchOut(b, middle);
    await expect.poll(async () => (await mapView(b)).width).toBeLessThan(start.width * 0.9);
    const zoomed = await mapView(b);
    await fingerDrag(b, middle, { x: -80, y: 0 });
    await expect.poll(async () => (await mapView(b)).centre).toBeGreaterThan(zoomed.centre);
    await fingerDrag(b, middle, { x: 0, y: -80 });
    await expect.poll(async () => (await mapView(b)).middle).toBeGreaterThan(zoomed.middle);
    expect(await wasNotReloaded(b)).toBe(true);
  }

  if (!editor) return;

  // Refused: the draft removes the cell A stands on
  await openAdmin(editor, world, 'map');
  await expect(editor.getByTestId('map-editor')).toBeVisible();
  await editor.getByTestId('map-cell-pick').selectOption('b9');
  await editor.getByTestId('map-cell-remove').click();
  await expect(editor.getByTestId('map-problems')).toContainText('стоят игроки');
  await expect(editor.getByTestId('map-publish-button')).toBeDisabled();
  await editor.getByTestId('map-reset').click();

  // Accepted: a5 becomes a checkpoint while both stand on the map; their pages show it live
  await markPage(a);
  await editor.getByTestId('map-cell-pick').selectOption('a5');
  await editor.getByTestId('map-cell-type').selectOption('checkpoint');
  await expect(editor.getByText('Ошибок нет: карту можно публиковать.')).toBeVisible();
  await editor.getByTestId('map-publish-comment').fill('Чекпоинт на верхней ветке');
  await editor.getByTestId('map-publish-button').click();
  await confirmButton(editor, 'Опубликовать').click();
  await expect(editor.getByText('Карта опубликована.')).toBeVisible();

  for (const page of [a, b]) {
    await expect(page.getByTestId('cell-a5')).toContainText('чекпоинт', live);
    expect(await wasNotReloaded(page)).toBe(true);
  }
  // Nobody moved: the tokens stand where they stood
  expect(await cellOf(b, walker.name)).toBe('cell-b9');
  expect(await cellOf(a, watcher.name)).toBe('cell-start');
});
