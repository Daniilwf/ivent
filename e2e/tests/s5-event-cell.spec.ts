import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { completeHere, rollAndStartHere, setUpSeason, signIn } from '../support/world.ts';

// Stage 5 (5.1, the thin slice): in a season of its own with items and events on, a completion stops on an event cell
// of the good deck, the drawn event is played at once (D-500) and its card stands in the player's turn; a second player
// sees the draw in the feed without a reload. On a phone and on a desktop.

const content = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'content');
const files = (dir: string) =>
  readdirSync(join(content, dir))
    .filter((f) => f.endsWith('.json'))
    .sort()
    .map((f) => readFileSync(join(content, dir, f), 'utf8'));

/** The design review's screenshots of the screen (DESIGN.md: 390×844 and 1440×900), outside git */
const designShot = (name: string) =>
  join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'test-artifacts', 'design', name);

/** start → c1 → c2 → c3 → every cell after it an event cell of the good deck → finish: any throw of 3+ stops on one */
function eventMap(): Schemas['MapGraphView'] {
  const ids = [
    'start',
    'c1',
    'c2',
    'e3',
    'e4',
    'e5',
    'e6',
    'e7',
    'e8',
    'e9',
    'e10',
    'e11',
    'e12',
    'finish',
  ];
  const cells: Schemas['CellView'][] = ids.map((id, i) => ({
    id,
    type:
      id === 'start'
        ? 'start'
        : id === 'finish'
          ? 'finish'
          : id.startsWith('e')
            ? 'event'
            : 'empty',
    ...(id.startsWith('e') ? { deck: 'good' } : {}),
    x: i * 110,
    y: 200,
  }));
  const edges: Schemas['EdgeView'][] = ids.slice(1).map((to, i) => ({
    from: ids[i] ?? 'start',
    to,
    isDefaultForward: true,
    isPrimaryBackward: true,
  }));
  return { cells, edges, zones: [] };
}

test('a stop on an event cell draws the event, its card is in the turn and the feed tells it to another player', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Ветреный', 'Наблюдатель'], start: false });
  const [player, other] = world.players;
  if (!player || !other) throw new Error('No players');
  const admin = world.adminApi;

  // The graph map, items and events on, three dice a completion: a throw walks 3 to 12 cells, onto an event cell
  const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
  const ruleset = {
    ...rules.ruleset,
    features: { ...rules.ruleset.features, mapMode: 'graph' as const, items: true, events: true },
    events: { defaultDeck: 'risky' as const, irlProof: { maxFiles: 5 } },
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
    map: eventMap(),
    comment: 'Клетки ивентов',
  });
  // The repository's events and decks (D-533) with the item an event gives; through the test tools (D-534)
  await admin.post(`/api/test/seasons/${world.seasonId}/content`, {
    objects: [
      ...files('events'),
      readFileSync(join(content, 'items', 'forced-genre.json'), 'utf8'),
    ],
    wheels: files('decks'),
    comment: 'Колоды ивентов',
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/status`, { to: 'active' });

  const watcher = await signIn(browser, other);
  await expect(watcher.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();

  const page = await signIn(browser, player);
  await expect(page.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();
  await expect(page.getByTestId('drawn-event')).toHaveCount(0);
  await rollAndStartHere(page);
  await completeHere(page);

  // The good deck holds one event: «Попутный ветер», played at once
  const card = page.getByTestId('drawn-event');
  await expect(card).toBeVisible();
  await expect(card).toContainText('Попутный ветер');
  await expect(card.getByTestId('drawn-event-kind')).toHaveText('Хороший ивент');
  // Drawn while the page was open: the card comes off the deck, then stands (the design review's screenshots)
  await expect(card).not.toHaveAttribute('data-drawing');
  await card.scrollIntoViewIfNeeded();
  await page.screenshot({ path: designShot(`drawn-event-${testInfo.project.name}.png`) });

  // The other player opens the feed from the menu without reloading the season: the completion with its event
  await watcher.getByTestId('nav-feed').first().click();
  const feed = watcher.getByTestId('feed');
  await expect(feed).toContainText(`${player.name} проходит`);
  await expect(feed).toContainText('Ивент «Попутный ветер»');
  await expect(watcher.getByTestId('drawn-event')).toHaveCount(0);
});
