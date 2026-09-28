import { expect, test } from '@playwright/test';
import {
  apiOf,
  commandsOf,
  fillCompletionHere,
  seasonOf,
  setUpSeason,
  signIn,
  type World,
} from '../support/world.ts';

// TESTING.md scenario 10 «Две вкладки одного игрока и параллельные клики»: the same player in two tabs clicks the same
// step in both at once (and twice in one): the command queue takes one (SPEC «Два действия одновременно»), the other is
// refused or repeats nothing; both tabs end on the same state, and the log holds each step once.

async function accepted(world: World, type: string): Promise<number> {
  return (await commandsOf(world)).filter((c) => c.commandType === type && !c.undone).length;
}

test('two tabs: the same step clicked in both at once happens once, and both tabs agree', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');
  const first = await signIn(browser, vasya);
  const second = await first.context().newPage();
  await second.goto('/');
  const tabs = [first, second];
  for (const tab of tabs) await expect(tab.getByTestId('roll')).toBeVisible();

  // The roll: a double click in one tab and a click in the other, together
  await Promise.all([first.getByTestId('roll').dblclick(), second.getByTestId('roll').click()]);
  for (const tab of tabs) await expect(tab.getByTestId('offer')).toBeVisible();
  const title = await first.locator('#roll-result-title').textContent();
  await expect(second.locator('#roll-result-title')).toHaveText(title ?? '');
  expect(await accepted(world, 'RollGame')).toBe(1);

  // The start, in both at once
  await Promise.all(tabs.map((tab) => tab.getByTestId('start').click()));
  for (const tab of tabs) await expect(tab.getByTestId('active-run')).toContainText(title ?? '');
  expect(await accepted(world, 'StartRun')).toBe(1);

  // The completion, sent from both at once
  for (const tab of tabs) await fillCompletionHere(tab, 'Сложная');
  await Promise.all(tabs.map((tab) => tab.getByTestId('complete-submit').click()));
  for (const tab of tabs) await expect(tab.getByTestId('roll')).toBeVisible();
  expect(await accepted(world, 'CompleteRun')).toBe(1);

  // One run's dice counted once, and both tabs show the same
  const view = await seasonOf(await apiOf(vasya), world.seasonId);
  const run = view.me?.lastCompleted;
  expect(run?.dice.length).toBeGreaterThan(0);
  expect(run?.dice.every((d) => d.sides === 6)).toBe(true);
  expect(view.players[0]?.points).toBe(run?.total);
  const dice = await first.getByTestId('last-dice').textContent();
  await expect(second.getByTestId('last-dice')).toHaveText(dice ?? '');
  await expect(second.getByTestId('last-dice')).toContainText(`итого ${String(run?.total)}`);
});
