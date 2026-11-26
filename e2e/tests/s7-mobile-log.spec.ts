import { expect, test } from '@playwright/test';
import { commandId, openAdmin, setUpSeason, signInHere } from '../support/world.ts';

test('a long adjustment comment fits the log and leaves the undo dialog controls reachable', async ({
  page,
}, info) => {
  const world = await setUpSeason({ players: ['Барсук'] });
  const [player] = world.players;
  if (!player) throw new Error('The log needs a player.');

  const id = commandId();
  const comment = 'ДлинноеСловоБезРазделителя'.repeat(19);
  const viewport =
    info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 };

  try {
    await world.adminApi.post(
      `/api/admin/seasons/${world.seasonId}/players/${player.playerId}/adjust`,
      {
        commandId: id,
        pointsDelta: 1,
        comment,
      },
    );
    await page.goto('/');
    await signInHere(page, world.admin);
    await openAdmin(page, world, 'log');
    expect(page.viewportSize()).toEqual(viewport);

    const row = page.getByTestId(`command-${id}`);
    await expect(row.getByText(`Комментарий: ${comment}`, { exact: true })).toBeVisible();
    await page.evaluate(() => document.fonts.ready);
    expect
      .soft(await page.evaluate(() => document.documentElement.scrollWidth))
      .toBeLessThanOrEqual(viewport.width);
    await page.screenshot({ path: info.outputPath('log.png'), scale: 'css' });

    const undo = row.getByTestId('undo');
    await undo.click();
    const dialog = page.getByRole('alertdialog');
    await expect(dialog.getByText(`Комментарий: ${comment}`, { exact: true })).toBeVisible();
    const content = await dialog.evaluate((node) => {
      const { left, right, top, bottom } = node.getBoundingClientRect();
      return {
        left,
        right,
        top,
        bottom,
        innerWidth: node.clientWidth,
        scrollWidth: node.scrollWidth,
      };
    });
    expect.soft(content.left).toBeGreaterThanOrEqual(0);
    expect.soft(content.right).toBeLessThanOrEqual(viewport.width);
    expect.soft(content.top).toBeGreaterThanOrEqual(0);
    expect.soft(content.bottom).toBeLessThanOrEqual(viewport.height);
    expect.soft(content.scrollWidth).toBeLessThanOrEqual(content.innerWidth);
    const cancel = dialog.getByRole('button', { name: 'Отмена', exact: true });
    const confirm = dialog.getByRole('button', { name: 'Откатить действие', exact: true });
    await expect(cancel).toBeFocused();
    await expect.soft(cancel).toBeInViewport({ ratio: 1 });
    await expect.soft(confirm).toBeInViewport({ ratio: 1 });
    await page.screenshot({ path: info.outputPath('undo.png'), scale: 'css' });

    await page.keyboard.press('Tab');
    await expect(confirm).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(dialog.getByTestId('undo-comment')).toBeFocused();
    await expect(dialog.getByTestId('undo-comment')).toBeInViewport();

    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(undo).toBeFocused();

    await undo.click();
    await confirm.click();
    await expect(dialog.getByTestId('undo-comment')).toBeFocused();
    await expect(dialog.getByRole('alert')).toHaveText('Напиши комментарий: он попадёт в лог.');
    await expect(dialog.getByRole('alert')).toBeInViewport({ ratio: 1 });
    await expect(confirm).toBeInViewport({ ratio: 1 });
    await page.screenshot({ path: info.outputPath('undo-comment.png'), scale: 'css' });
  } finally {
    await world.adminApi.dispose();
  }
});
