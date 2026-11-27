import { expect, test, type Page } from '@playwright/test';
import { live, openAdmin, setUpSeason, signIn } from '../support/world.ts';

// TESTING.md scenario 12 «Страница правил»: the rules page is built from the season's current config (SPEC «Правила на
// сайте»): the numbers of docs/ruleset.default.json first; the admin changes two of them in the admin's rules editor —
// the page shows the new numbers without a reload, and the history says when, who, what was and what is.

function dieOf(page: Page, difficulty: string) {
  return page.getByTestId('rules-dice').getByRole('row', { name: difficulty });
}

test('rules contents scroll to the selected section and Forward returns there', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Игрок'] });
  const [player] = world.players;
  if (!player) throw new Error('No player.');
  const page = await signIn(browser, player);
  await page.getByTestId('nav-rules').first().click();

  const contents = page.getByRole('navigation', { name: 'На этой странице' });
  const roll = contents.getByRole('link', { name: 'Ролл игры' });
  const expectRollInView = async () => {
    await expect(page).toHaveURL(/\/rules#rules-roll$/);
    await expect
      .poll(() =>
        page.evaluate(async () => {
          await new Promise<void>((resolve) => {
            requestAnimationFrame(() =>
              requestAnimationFrame(() => {
                resolve();
              }),
            );
          });
          const header = document.querySelector('header.sticky')?.getBoundingClientRect();
          const title = document.querySelector('#rules-roll-title')?.getBoundingClientRect();
          return (
            !!header &&
            !!title &&
            window.scrollY > 0 &&
            title.top >= header.bottom &&
            title.bottom <= window.innerHeight
          );
        }),
      )
      .toBe(true);
  };

  await roll.click();
  await expectRollInView();
  await page.goBack();
  await expect(page).toHaveURL(/\/rules$/);
  await expect(roll).toBeInViewport();
  await page.goForward();
  await expectRollInView();
  await page.reload();
  await expectRollInView();
  await page.screenshot({ path: testInfo.outputPath('rules-roll-anchor.png') });
});

test('rules: the page shows the config, then the admin’s new numbers and the change in the history', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Вася'] });
  const [vasya] = world.players;
  if (!vasya) throw new Error('No player.');

  // The numbers of the default config: d4 on normal, d6 on hard; 10 points for the second finisher
  const page = await signIn(browser, vasya);
  await page.getByTestId('nav-rules').first().click();
  await expect(page).toHaveURL(/\/rules$/);
  await expect(dieOf(page, 'Нормальная')).toContainText('d4');
  await expect(dieOf(page, 'Сложная')).toContainText('d6');
  const second = page.getByTestId('rules-bonuses').getByRole('listitem').first();
  await expect(second).toHaveText('2-й на финише+10 очков');
  await expect(page.getByTestId('rules-history')).toContainText(
    'Правила ещё не менялись с начала сезона.',
  );

  // The admin edits the config as JSON: a d8 on hard, 12 points for the second finisher
  const admin = await signIn(browser, world.admin);
  await openAdmin(admin, world, 'rules');
  const editor = admin.getByTestId('rules-editor');
  const ruleset = JSON.parse(await editor.inputValue()) as {
    reward: { dieByDifficulty: { hard: { sides: number } } };
    finish: { bonusByOrder: number[] };
  };
  ruleset.reward.dieByDifficulty.hard.sides = 8;
  ruleset.finish.bonusByOrder[0] = 12;
  await editor.fill(JSON.stringify(ruleset, null, 2));
  await admin.getByTestId('rules-save').click();
  await expect(admin.getByTestId('rules-outcome')).toBeVisible();
  await expect(admin.getByTestId('rules-version')).toContainText('2');

  // The player's page follows without a reload
  await expect(dieOf(page, 'Сложная')).toContainText('d8', live);
  await expect(dieOf(page, 'Нормальная')).toContainText('d4');
  await expect(second).toHaveText('2-й на финише+12 очков');
  const history = page.getByTestId('rules-history');
  await expect(history).toContainText('Версия 2');
  await expect(history).toContainText(`Поменял ${world.admin.name}`);
  // The change's own line, not the version's that holds it
  const hard = history
    .getByRole('listitem')
    .filter({ hasText: 'Кубик на сложной', hasNotText: 'Версия' });
  await expect(hard.locator('del')).toHaveText('6');
  await expect(hard.locator('ins')).toHaveText('8');
  await expect(history.getByRole('listitem').filter({ hasText: 'Версия 1' })).toContainText(
    'Сезон начался с этими правилами',
  );
});
