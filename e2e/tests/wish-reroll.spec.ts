import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { holdingTheClock } from '../support/clock.ts';
import { apiOf, rollAndStart, seasonOf, setUpSeason, signIn } from '../support/world.ts';

// D-325, D-261: «Реролл по желанию» — a free give-up of a game of a genre the season's rules list, within the tech
// reroll window: the turn card offers it only while the server says it is open, a window says what it does, a yes
// brings a new roll with no penalty. Without listed genres (the default) the button is not there at all.

test('wish reroll: hidden without listed genres; with them a confirmed, free new roll', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');
  const player = await apiOf(vasya);
  const url = (action: string) => `/api/seasons/${world.seasonId}/${action}`;

  await holdingTheClock(async () => {
    // The default rules list no genre: the active run offers the tech reroll, not the wish reroll
    const page = await signIn(browser, vasya);
    await page.getByTestId('roll').click();
    await expect(page.getByTestId('offer').or(page.getByTestId('choice'))).toBeVisible();
    if (await page.getByTestId('choice').isVisible())
      await page.getByTestId('choice').locator('[data-testid^="option-"]').first().click();
    await page.getByTestId('start').click();
    await expect(page.getByTestId('tech-reroll')).toBeVisible();
    await expect(page.getByTestId('wish-reroll')).toHaveCount(0);
    await player.post(url('drop'));

    // The admin lists every genre of the pool; the list is fixed at the roll, so the next roll comes after it
    const pool = await world.adminApi.get<Schemas['PoolGameView'][]>('/api/pool');
    const tags = [...new Set(pool.flatMap((game) => game.tags))];
    const rules = await world.adminApi.get<Schemas['RulesView']>(
      `/api/seasons/${world.seasonId}/rules`,
    );
    await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
      expectedVersion: rules.version,
      ruleset: { ...rules.ruleset, roll: { ...rules.ruleset.roll, wishRerollTags: tags } },
    });

    // A game the pool's other tests added may have no genre: a free tech reroll until one has
    await rollAndStart(player, world.seasonId);
    let view = await seasonOf(player, world.seasonId);
    for (let tries = 0; view.me?.wishRerollOpen !== true; tries++) {
      if (tries >= 5) throw new Error('No game of a listed genre came up.');
      await player.post(url('tech-reroll'), { reason: 'doesNotLaunch' });
      view = await seasonOf(player, world.seasonId);
      const choice = view.me?.choice;
      const option = choice?.options.find((o) => o.game !== null);
      if (choice && option)
        await player.post(url('choose'), { choiceId: choice.id, optionId: option.id });
      await player.post(url('start'));
      view = await seasonOf(player, world.seasonId);
    }
    const before = view.me.activeRun?.game.title;
    const mine = (v: Schemas['SeasonView']) => v.players.find((p) => p.id === vasya.playerId);
    const pointsBefore = mine(view)?.points;
    const cellBefore = mine(view)?.cellId;

    // The card offers it; the window says what it does; a cancel sends nothing
    await page.reload();
    await expect(page.getByTestId('active-run')).toBeVisible();
    await page.getByTestId('wish-reroll').click();
    const dialog = page.getByTestId('wish-reroll-dialog');
    await expect(dialog).toContainText('Реролл по желанию');
    await expect(dialog).toContainText('штрафа нет');
    await expect(dialog).toContainText('только в окне тех-реролла');
    await dialog.getByTestId('wish-reroll-cancel').click();
    await expect(dialog).toHaveCount(0);
    await expect(page.getByTestId('active-run')).toBeVisible();

    // A yes: a new roll at once, no points and no cells lost (the drop before cost its own)
    await page.getByTestId('wish-reroll').click();
    await page.getByTestId('wish-reroll-confirm').click();
    await expect(page.getByTestId('offer').or(page.getByTestId('choice'))).toBeVisible();
    view = await seasonOf(player, world.seasonId);
    expect(view.me?.activeRun ?? null).toBeNull();
    expect(mine(view)?.points).toBe(pointsBefore);
    expect(mine(view)?.cellId).toBe(cellBefore);
    expect(view.me?.offer?.title).not.toBe(before);
  });
});
