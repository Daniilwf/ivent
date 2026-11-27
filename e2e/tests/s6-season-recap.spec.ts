import { readFile, writeFile } from 'node:fs/promises';
import { expect, test, type Page } from '@playwright/test';
import { PNG } from 'pngjs';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  complete,
  live,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

test('only a participant opens their own season recap and downloads it as PNG', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({
    players: ['Альфа', 'Хранитель_настольного_сезона'],
    spectator: true,
    start: false,
  });
  const player = world.players[1];
  const spectator = world.spectator;
  if (!player || !spectator) throw new Error('Missing test accounts.');

  const apiPath = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const recapPath = `/seasons/${world.seasonId}/recap`;
  const profilePath = `/users/${player.id}`;
  const linkName = `Твой сезон, ${world.seasonName}`;

  async function setStatistics(enabled: boolean) {
    const { version, ruleset } = await world.adminApi.get<Schemas['RulesView']>(`${apiPath}/rules`);
    await world.adminApi.put(`${adminPath}/rules`, {
      expectedVersion: version,
      ruleset: {
        ...ruleset,
        features: { ...ruleset.features, seasonStatistics: enabled },
      },
    });
  }

  await setStatistics(true);
  await world.adminApi.post(`${adminPath}/status`, { to: 'active' });
  await world.adminApi.post(`/api/test/seasons/${world.seasonId}/scenarios/finish-soon`, {
    player: player.login,
  });
  const playerApi = await apiOf(player);
  let completedRun: Schemas['CompletedRunView'] | null | undefined;
  try {
    await rollAndStart(playerApi, world.seasonId);
    await complete(playerApi, world.seasonId, 'normal', { review: { rating: 8 } });
    completedRun = (await seasonOf(playerApi, world.seasonId)).me?.lastCompleted;
  } finally {
    await playerApi.dispose();
  }
  if (!completedRun) throw new Error('The game completion is missing.');
  const correctedHours = completedRun.game.hours === 12.75 ? 15.125 : 12.75;
  const displayedHours = correctedHours.toLocaleString('ru-RU');

  const ownerPage = await signIn(browser, player);
  const spectatorPage = await signIn(browser, spectator);
  try {
    await ownerPage.goto(profilePath);
    const range = ownerPage.getByText(/^\d+–\d+$/u).first();
    await expect(range).toHaveCSS('white-space', 'nowrap');
    await ownerPage.getByRole('link', { name: linkName }).click();
    await expect(ownerPage).toHaveURL(new RegExp(`${recapPath}$`));
    await expect(ownerPage.getByRole('heading', { name: 'Твой сезон', level: 1 })).toBeFocused();

    const card = ownerPage.getByRole('article', { name: player.name });
    await expect(card.getByText(world.seasonName)).toBeVisible();
    await expect(card.getByText('из 10')).toHaveCount(2);
    await expect(card.getByText('Часы', { exact: true })).toBeVisible();
    await world.adminApi.post(`${adminPath}/runs/${completedRun.id}/hours`, {
      hours: correctedHours,
      comment: 'Проверка часов в сводке и картинке',
    });
    await expect(card.getByText(displayedHours, { exact: true })).toBeVisible(live);
    const ratings = card
      .locator('section')
      .filter({ has: ownerPage.getByRole('heading', { name: 'Оценки' }) });
    await expect(ratings.getByText('8', { exact: true })).toHaveCount(2);
    await expect(card.getByRole('heading', { name: 'Броски' })).toBeVisible();
    await expect(card.getByText('Самый удачный', { exact: true })).toBeVisible();
    await expect(card.getByText('Самый неудачный', { exact: true })).toBeVisible();
    const season = (await (await ownerPage.request.get(apiPath)).json()) as Schemas['SeasonView'];
    const own = season.players.find(({ id }) => id === player.playerId);
    const standing = season.leaderboard.find(({ playerId }) => playerId === own?.id);
    if (!own || !standing) throw new Error('The player or their standing is missing.');
    expect(own.token).toBe(1);
    expect(standing).toMatchObject({ place: 1, provisional: true });
    const place = '1-е место (предварительно)';
    const points = card.getByText(
      new RegExp(`^${standing.points.toLocaleString('ru-RU')} оч`, 'u'),
    );
    await expect(card.getByText(place, { exact: true })).toBeVisible();
    await expect(points).toBeVisible();
    await expect(card.getByText('Игровой ивент', { exact: true })).toBeVisible();
    const token = await ownerPage.evaluate((index) => {
      const css = getComputedStyle(document.documentElement);
      return css.getPropertyValue(`--color-token-${(index + 1).toString()}`).trim();
    }, own.token);
    const rgb = [1, 3, 5].map((i) => Number.parseInt(token.slice(i, i + 2), 16));
    await expect(card.getByTestId('recap-player-accent')).toHaveCSS(
      'background-color',
      `rgb(${rgb.join(', ')})`,
    );
    await ownerPage.screenshot({ path: testInfo.outputPath('season-recap.png') });
    expect(
      await ownerPage.evaluate(() => document.documentElement.scrollWidth),
    ).toBeLessThanOrEqual(ownerPage.viewportSize()?.width ?? 0);

    const captions = await ownerPage.evaluateHandle(() => {
      const written: { text: string; y: number }[] = [];
      // eslint-disable-next-line @typescript-eslint/unbound-method -- forward the original canvas context below
      const fill = CanvasRenderingContext2D.prototype.fillText;
      CanvasRenderingContext2D.prototype.fillText = function (...args) {
        written.push({ text: args[0], y: args[2] });
        fill.apply(this, args);
      };
      return written;
    });
    const action = ownerPage.getByRole('button', { name: 'Скачать картинку для чата' });
    const [download] = await Promise.all([
      ownerPage.waitForEvent('download'),
      action.press('Enter'),
    ]);
    expect(download.suggestedFilename()).toBe('Твой сезон.png');
    const path = await download.path();
    if (!path) throw new Error('No download path.');
    const png = await readFile(path);
    await writeFile(testInfo.outputPath('chat-light.png'), png);
    expect(png.subarray(0, 8).toString('hex')).toBe('89504e470d0a1a0a');
    expect(png.readUInt32BE(16)).toBeGreaterThan(700);
    expect(png.readUInt32BE(20)).toBeGreaterThan(400);
    const written = await captions.jsonValue();
    expect(written.map(({ text }) => text)).toEqual(
      expect.arrayContaining([
        'Часы',
        displayedHours,
        'часа',
        'Броски',
        'Самый удачный',
        'Самый неудачный',
        place,
        await points.textContent(),
        'Игровой ивент',
      ]),
    );
    const captionY = (text: string) => written.find((caption) => caption.text === text)?.y ?? NaN;
    expect(captionY('Часы')).toBeLessThan(captionY(displayedHours));
    const rollExplanation = written.find(({ text }) => text.startsWith('Выпало '));
    expect(captionY('Самый удачный')).toBeLessThan(rollExplanation?.y ?? NaN);
    expect(cardHasTokenAccent(png, rgb)).toBe(true);
    await captions.dispose();
    // The card follows the sender's theme (D-940): light in the light theme, dark after «Тёмная»
    expect(cardLightness(png)).toBeGreaterThan(0.8);
    await ownerPage.evaluate(() => {
      localStorage.setItem('ge.theme', 'dark');
    });
    await ownerPage.reload();
    const darkPng = await downloadCard(ownerPage);
    await writeFile(testInfo.outputPath('chat-dark.png'), darkPng);
    expect(cardLightness(darkPng)).toBeLessThan(0.25);
    expect(cardHasTokenAccent(darkPng, rgb)).toBe(true);
    await ownerPage.evaluate(() => {
      localStorage.removeItem('ge.theme');
    });
    const proofs = await apiOf(player);
    try {
      await proofs.post(`${apiPath}/runs/${completedRun.id}/proof`, {
        links: ['https://example.com/credits.png'],
      });
    } finally {
      await proofs.dispose();
    }
    await world.adminApi.post(`${adminPath}/runs/${completedRun.id}/approve`, {
      comment: 'Пруф проверен',
    });
    await expect(card.getByText('1-е место', { exact: true })).toBeVisible(live);
    await expect(card.getByText(place, { exact: true })).toHaveCount(0);

    await spectatorPage.goto(profilePath);
    await expect(spectatorPage.getByRole('heading', { name: player.name })).toBeVisible();
    await expect(spectatorPage.getByRole('link', { name: linkName })).toHaveCount(0);
    await spectatorPage.goto(recapPath);
    await expect(
      spectatorPage.getByRole('heading', { name: 'Сводка сезона недоступна' }),
    ).toBeVisible();
    await expect(
      spectatorPage.getByRole('button', { name: 'Скачать картинку для чата' }),
    ).toHaveCount(0);

    await setStatistics(false);
    await expect(ownerPage.getByRole('heading', { name: 'Сводка сезона недоступна' })).toBeVisible(
      live,
    );
    await expect(card).toHaveCount(0);
    await expect(action).toHaveCount(0);
  } finally {
    await ownerPage.context().close();
    await spectatorPage.context().close();
    await world.adminApi.dispose();
  }
});

/** Downloads the chat card of the open recap page */
async function downloadCard(page: Page): Promise<Buffer> {
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('button', { name: 'Скачать картинку для чата' }).click(),
  ]);
  const path = await download.path();
  if (!path) throw new Error('No download path.');
  return readFile(path);
}

/** The lightness (0–1) of the card's own background, just inside its outline at the top left */
function cardLightness(file: Buffer): number {
  const png = PNG.sync.read(file);
  const at = (png.width * 24 + 24) * 4;
  const [r = 0, g = 0, b = 0] = png.data.subarray(at, at + 3);
  return (r + g + b) / (3 * 255);
}

/** The token marker starts near the card's left edge, above the statistics. */
function cardHasTokenAccent(file: Buffer, rgb: number[]): boolean {
  const png = PNG.sync.read(file);
  const x = 64;
  for (let y = 24; y < png.height / 3; y++) {
    const at = (png.width * y + x) * 4;
    if (rgb.every((channel, i) => png.data[at + i] === channel) && png.data[at + 3] === 255) {
      return true;
    }
  }
  return false;
}
