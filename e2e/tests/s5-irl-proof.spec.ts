import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, live, rollAndStart, seasonOf, setUpSeason, signIn } from '../support/world.ts';

const picture = readFileSync(
  fileURLToPath(new URL('../../content/map.demo.background.png', import.meta.url)),
);
const karaoke = readFileSync(
  fileURLToPath(new URL('../../content/events/karaoke.json', import.meta.url)),
  'utf8',
);
const shot = (name: string) =>
  join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'test-artifacts', 'design', name);

test('an IRL proof keeps its materials through rejection and resubmission until the admin approves it', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Исполнитель', 'Наблюдатель'], start: false });
  const [player, observer] = world.players;
  if (!player || !observer) throw new Error('No players');
  const admin = world.adminApi;
  const path = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const rules = await admin.get<Schemas['RulesView']>(`${path}/rules`);
  await admin.put(`${adminPath}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: { ...rules.ruleset.features, items: true, events: true },
      events: { defaultDeck: 'bad', irlProof: { maxFiles: 1 } },
    },
  });
  await admin.post(`/api/test/seasons/${world.seasonId}/content`, {
    objects: [karaoke],
    wheels: [
      JSON.stringify({
        id: 'bad',
        name: 'Плохие ивенты',
        deck: 'bad',
        entries: [{ objectId: 'karaoke', weight: 1 }],
      }),
    ],
    comment: 'IRL-ивент',
  });
  await admin.post(`${adminPath}/status`, { to: 'active' });
  const owner = await apiOf(player);
  const other = await apiOf(observer);
  const page = await signIn(browser, player);
  const watcher = await signIn(browser, observer);
  const externalRequests: string[] = [];
  page.on('request', (request) => {
    if (new URL(request.url()).hostname === 'example.com') externalRequests.push(request.url());
  });
  try {
    await rollAndStart(owner, world.seasonId);
    await owner.post(`${path}/drop`);
    await expect
      .poll(async () => (await seasonOf(owner, world.seasonId)).me?.manualEffects.length)
      .toBe(1);
    const effect = (await seasonOf(owner, world.seasonId)).me?.manualEffects[0];
    if (!effect?.irl) throw new Error('No IRL effect');
    const task = page.getByTestId(`manual-effect-${effect.id}`);
    await expect(task.getByRole('heading', { name: 'Караоке' })).toBeVisible(live);
    await expect(task).toContainText('Спой песню на выбор остальных');
    // An IRL task does not block a new turn.
    await expect(page.getByTestId('roll')).toBeEnabled();
    const video = `https://example.com/private-irl-${world.seasonId}`;
    const picker = task.getByTestId('irl-proof-file');
    await picker.focus();
    await expect(picker).toBeFocused();
    await picker.setInputFiles({ name: 'karaoke.png', mimeType: 'image/png', buffer: picture });
    await expect(task.getByRole('img')).toHaveCount(1);
    await expect(task.getByTestId('irl-proof-file')).toHaveCount(0);
    await expect(task.getByRole('textbox', { name: /ссылка на видео/i })).toBeFocused();
    await task.getByRole('textbox', { name: /ссылка на видео/i }).fill(video);
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({
      path: shot(`s5-21-irl-form-${info.project.name}.png`),
      fullPage: true,
    });
    await task.getByRole('button', { name: 'Отправить пруф', exact: true }).click();
    await expect(task.getByText('Пруф на проверке', { exact: true })).toBeVisible(live);
    await expect
      .poll(() => page.evaluate(() => document.activeElement?.textContent ?? ''))
      .toContain('Пруф на проверке');
    await expect(task.getByRole('button', { name: 'Отправить пруф', exact: true })).toHaveCount(0);
    await expect(task.getByRole('link', { name: 'example.com' })).toHaveAttribute('href', video);
    await expect(task.getByRole('link', { name: 'example.com' })).toHaveAttribute(
      'rel',
      /noopener/,
    );
    await expect(task.getByRole('link', { name: 'example.com' })).toHaveAttribute(
      'rel',
      /noreferrer/,
    );
    await page.screenshot({
      path: shot(`s5-21-irl-pending-${info.project.name}.png`),
      fullPage: true,
    });
    const pending = (await seasonOf(owner, world.seasonId)).me?.manualEffects[0]?.irlProof;
    expect(pending?.status).toBe('pending');
    expect(pending?.files).toHaveLength(1);
    const file = pending?.files[0];
    if (!file) throw new Error('No submitted picture');
    // D-124/D-820 hide proof fields from other players. D-108 still serves known file URLs to signed-in accounts.
    const foreignView = await seasonOf(other, world.seasonId);
    const foreignFeed = await other.get<unknown>(`${path}/feed`);
    expect(foreignFeed).toHaveProperty('entries');
    for (const json of [JSON.stringify(foreignView), JSON.stringify(foreignFeed)]) {
      expect(json).not.toContain(video);
      expect(json).not.toContain(file.id);
    }
    await expect(watcher.getByTestId(`manual-effect-${effect.id}`)).toHaveCount(0);
    await expect(watcher.locator(`a[href="${video}"]`)).toHaveCount(0);
    await admin.post(`${adminPath}/effects/${effect.id}/irl-proof/review`, {
      approved: false,
      comment: 'Добавь голос в записи',
    });
    await expect(task.getByText('Пруф отклонён', { exact: true })).toBeVisible(live);
    await expect(task).toContainText('Добавь голос в записи');
    await expect(task.getByRole('textbox', { name: /ссылка на видео/i })).toHaveValue(video);
    await expect(task.getByRole('img')).toHaveCount(1);
    await page.screenshot({
      path: shot(`s5-21-irl-rejected-${info.project.name}.png`),
      fullPage: true,
    });
    const corrected = `${video}?version=2`;
    await task.getByRole('textbox', { name: /ссылка на видео/i }).fill(corrected);
    await task.getByRole('button', { name: 'Отправить пруф', exact: true }).click();
    await expect(task.getByText('Пруф на проверке', { exact: true })).toBeVisible(live);
    expect((await seasonOf(owner, world.seasonId)).me?.manualEffects[0]?.irlProof?.link).toBe(
      corrected,
    );
    const approval = await admin.post(`${adminPath}/effects/${effect.id}/irl-proof/review`, {
      approved: true,
    });
    expect(approval.events.some((event) => event.type === 'irl-proof-reviewed')).toBe(true);
    await expect(task).toHaveCount(0, live);
    expect((await seasonOf(owner, world.seasonId)).me?.manualEffects).toHaveLength(0);
    expect(externalRequests).toEqual([]);
  } finally {
    if (!page.isClosed()) await page.context().close();
    if (!watcher.isClosed()) await watcher.context().close();
    await owner.dispose();
    await other.dispose();
    await admin.dispose();
  }
});
