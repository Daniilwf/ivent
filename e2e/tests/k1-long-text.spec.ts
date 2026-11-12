import { expect, test, type Locator, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, setUpSeason, signIn, uniqueTag } from '../support/world.ts';

// K1A (exploratory testing of stages 1 and 2, the phone): the server takes a 200-character title without spaces, a long
// completion condition and note, and a 64-character name without spaces; the pool card and the game page wrap them
// with no sideways scroll at 390 px (DESIGN.md «Иерархия и компоновка»). A card's title and the profile's links are
// tap targets of at least 44 px on a phone (DESIGN.md «Компоненты и состояния»).

async function noSidewaysScroll(page: Page) {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
}

async function tall(target: Locator) {
  const box = await target.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
}

test('long unbroken text wraps on the pool card and the game page', async ({
  browser,
}, testInfo) => {
  const phone = testInfo.project.name === 'phone';
  const tag = uniqueTag();
  // Not a category on the wheel: the game never comes up in another test's roll
  const name = `Константинопольский${'Переподвыподверт'.repeat(3)}`.slice(0, 64);
  const world = await setUpSeason({ players: [name] });
  const [author] = world.players;
  if (!author) throw new Error('No player.');
  const title = `Сверхдлинное${tag}${'Ж'.repeat(200)}`.slice(0, 200);
  const api = await apiOf(author);
  await api.post('/api/pool', {
    title,
    tags: [`k1-${tag}`],
    completionCondition: 'Условие'.repeat(40),
    note: 'Заметка'.repeat(50),
  });
  const games = await api.get<Schemas['PoolGameView'][]>('/api/pool');
  const game = games.find((g) => g.title === title);
  if (!game) throw new Error('The game is not in the pool.');

  const page = await signIn(browser, author);
  await page.goto('/pool');
  await page.getByLabel('Поиск по названию').fill(`Сверхдлинное${tag}`);
  const card = page.getByTestId('pool-game').filter({ hasText: `Сверхдлинное${tag}` });
  await expect(card).toHaveCount(1);
  await noSidewaysScroll(page);
  const cardBox = await card.boundingBox();
  const viewport = page.viewportSize();
  expect((cardBox?.x ?? 0) + (cardBox?.width ?? 0)).toBeLessThanOrEqual(viewport?.width ?? 0);
  if (phone) await tall(card.getByRole('link').first());

  await page.goto(`/games/${game.id}`);
  await expect(page.getByRole('heading', { level: 1 })).toContainText(`Сверхдлинное${tag}`);
  await noSidewaysScroll(page);

  await page.goto(`/users/${author.id}`);
  const season = page.getByRole('link', { name: world.seasonName });
  await expect(season.first()).toBeVisible();
  await noSidewaysScroll(page);
  if (phone) await tall(season.first());
  await api.dispose();
});
