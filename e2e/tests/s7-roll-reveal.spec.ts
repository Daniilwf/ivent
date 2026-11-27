import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { holdingTheClock } from '../support/clock.ts';
import { apiOf, live, seasonOf, setUpSeason, signIn, signInHere } from '../support/world.ts';

const variants = [
  { motion: 'no-preference', action: 'Крутить игру' },
  { motion: 'reduce', action: 'Крутить игру' },
  { motion: 'reduce', action: 'Показать игру сразу' },
] as const;

for (const { motion, action } of variants) {
  test.describe(`${motion} / ${action}`, () => {
    test.use({ reducedMotion: motion, contextOptions: { reducedMotion: motion } });

    test('the two-step roll survives reload and names the same game to a spectator only after reveal', async ({
      page,
      browser,
    }, info) => {
      const world = await setUpSeason({ players: ['Лиса'], spectator: true });
      const player = world.players[0];
      if (!player || !world.spectator) throw new Error('An owner and a spectator are required.');
      const api = await apiOf(player);
      const viewer = await signIn(browser, world.spectator);
      const rollPath = `/api/seasons/${world.seasonId}/roll`;
      const revealPath = `${rollPath}/reveal`;
      let rolls = 0;
      let reveals = 0;
      page.on('request', (request) => {
        if (request.method() !== 'POST') return;
        const path = new URL(request.url()).pathname;
        if (path === rollPath) rolls++;
        if (path === revealPath) reveals++;
      });

      try {
        await page.goto('/');
        await signInHere(page, player);
        await viewer.goto(`/seasons/${world.seasonId}/feed`);
        const feed = viewer.getByTestId('feed');
        await expect(feed).toBeVisible();
        expect(
          await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches),
        ).toBe(motion === 'reduce');

        await holdingTheClock(async () => {
          const genreButton = page.getByRole('button', { name: 'Крутить жанр', exact: true });
          const gameButton = page.getByRole('button', { name: 'Крутить игру', exact: true });
          const offer = page.getByTestId('offer');
          await expect(genreButton).toBeVisible();
          await genreButton.click();
          await expect(gameButton).toBeEnabled(live);

          const held = (await seasonOf(api, world.seasonId)).me;
          const saved = held?.roll;
          const selection = held?.offer;
          if (!saved?.revealId || !selection)
            throw new Error('No saved result after the first press.');
          const category = page.getByTestId('roll-genre');
          await expect(category).toContainText(`Жанр: ${saved.category}`);
          expect(saved.revealed).toBe(false);
          expect(rolls).toBe(1);
          expect(reveals).toBe(0);
          await expect(offer).toHaveCount(0);
          await expect(feed).toContainText('Лиса крутит колесо игр', live);
          await expect(feed).not.toContainText(selection.title);

          await page.reload();
          await expect(gameButton).toBeEnabled();
          await expect(category).toContainText(`Жанр: ${saved.category}`);
          await expect(offer).toHaveCount(0);
          expect(reveals).toBe(0);
          expect((await seasonOf(api, world.seasonId)).me?.roll).toMatchObject({
            sequence: saved.sequence,
            revealId: saved.revealId,
            revealed: false,
          });
          await expect(feed).toContainText('Лиса крутит колесо игр');
          await expect(feed).not.toContainText(selection.title);
          if (motion === 'no-preference')
            await page.screenshot({
              path: info.outputPath('genre-held-after-reload.png'),
              scale: 'css',
            });

          const [response] = await Promise.all([
            page.waitForResponse(
              (response) =>
                response.request().method() === 'POST' &&
                new URL(response.url()).pathname === revealPath,
            ),
            page.getByRole('button', { name: action, exact: true }).click(),
          ]);
          // Chromium can omit a streamed fetch body from request events. The server only writes this
          // event after matching the submitted reveal ID against the pending roll.
          expect(response.ok()).toBe(true);
          const receipt = (await response.json()) as Schemas['CommandResponse'];
          expect(receipt.duplicate).toBe(false);
          expect(receipt.events.map((event) => event.type)).toEqual(['roll-game-shown']);
          await expect(offer).toBeVisible(live);
          await expect(page.locator('#roll-result-title')).toHaveText(selection.title);
          await expect(feed).toContainText(selection.title, live);
          await expect(feed).not.toContainText('Лиса крутит колесо игр');
          expect(rolls).toBe(1);
          expect(reveals).toBe(1);
          const shown = (await seasonOf(api, world.seasonId)).me;
          expect(shown?.roll).toMatchObject({
            sequence: saved.sequence,
            revealId: saved.revealId,
            revealed: true,
          });
          expect(shown?.offer?.id).toBe(selection.id);
          if (motion === 'no-preference')
            await page.screenshot({ path: info.outputPath('game-revealed.png'), scale: 'css' });
        });
      } finally {
        await viewer.context().close();
        await api.dispose();
        await world.adminApi.dispose();
      }
    });
  });
}
