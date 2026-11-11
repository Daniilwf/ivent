import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { confirmButton, openAdmin, setUpSeason, signIn } from '../support/world.ts';

// The map's background (SPEC «Редактор»: загрузка фона; D-277, D-278, MP30): in a season of its own on the graph map,
// the admin uploads a picture in the editor and publishes it with the map (desktop: the editor is for a computer,
// D-313); the player, whose page stays open, sees the picture under the cells. On a phone the admin's upload goes
// through the same endpoint by API and the player sees it on the phone's map.

const picture = readFileSync(
  fileURLToPath(new URL('../../content/map.demo.background.png', import.meta.url)),
);

/** start → c1 → … → c6 → finish across a picture of 2070 × 1520 */
function lineMap(): Schemas['MapGraphView'] {
  const ids = ['start', 'c1', 'c2', 'c3', 'c4', 'c5', 'c6', 'finish'];
  return {
    cells: ids.map((id, i) => ({
      id,
      type: i === 0 ? 'start' : i === ids.length - 1 ? 'finish' : 'empty',
      x: 155 + i * 250,
      y: 760,
    })),
    edges: ids.slice(1).map((to, i) => ({
      from: ids[i] ?? 'start',
      to,
      isDefaultForward: true,
      isPrimaryBackward: true,
    })),
    zones: [],
  };
}

test('the admin uploads a map background and publishes it, the player sees it under the cells', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Художник'], start: false });
  const [player] = world.players;
  if (!player) throw new Error('No player');
  const admin = world.adminApi;
  const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
  await admin.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, mapMode: 'graph' } },
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/map/publish`, {
    map: lineMap(),
    comment: 'Карта без фона',
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/status`, { to: 'active' });

  // The player's page stays open: the map without a picture is the zones' board
  const page = await signIn(browser, player);
  await expect(page.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();
  await expect(page.getByTestId('cell-c3')).toBeAttached();
  await expect(page.locator('image[data-map-background]')).toHaveCount(0);

  if (testInfo.project.name.startsWith('desktop')) {
    const editor = await signIn(browser, world.admin);
    await openAdmin(editor, world, 'map');
    const panel = editor.getByTestId('map-background');
    await expect(panel.getByTestId('map-background-none')).toBeVisible();
    await panel.getByTestId('map-background-file').setInputFiles({
      name: 'map.png',
      mimeType: 'image/png',
      buffer: picture,
    });
    await expect(panel.getByTestId('map-background-size')).toHaveText('2070 × 1520 пикселей');
    await expect(editor.getByTestId('canvas-background')).toBeVisible();
    await expect(editor.getByText('Ошибок нет: карту можно публиковать.')).toBeVisible();
    await editor.getByTestId('map-publish-comment').fill('Фон карты');
    await editor.getByTestId('map-publish-button').click();
    await confirmButton(editor, 'Опубликовать').click();
    await expect(editor.getByText('Карта опубликована.')).toBeVisible();
  } else {
    const stored = await admin.upload(
      `/api/admin/seasons/${world.seasonId}/map/background`,
      picture,
      'map.png',
    );
    await admin.post(`/api/admin/seasons/${world.seasonId}/map/publish`, {
      map: {
        ...lineMap(),
        background: { fileId: stored.id, width: stored.width, height: stored.height },
      },
      comment: 'Фон карты',
    });
  }

  // Without a reload: the publication reaches the open page, the picture is under the cells and loads
  const background = page.locator('image[data-map-background]');
  await expect(background).toHaveCount(1);
  const href = await background.getAttribute('href');
  expect(href).toMatch(/^\/api\/files\/[0-9a-f-]+$/);
  await expect(background).toHaveClass(/opacity-100/);
  const file = await page.request.get(href ?? '');
  expect(file.headers()['content-type']).toBe('image/webp');
  await expect(page.getByTestId('cell-c3')).toBeAttached();
});
