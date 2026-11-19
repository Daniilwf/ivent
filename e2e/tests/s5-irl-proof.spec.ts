import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  live,
  openAdmin,
  rollAndStart,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

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
  const moderator = await signIn(browser, world.admin);
  const externalRequests: string[] = [];
  for (const proofReader of [page, moderator]) {
    proofReader.on('request', (request) => {
      if (new URL(request.url()).hostname === 'example.com') externalRequests.push(request.url());
    });
  }
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
    await watcher.goto(`/seasons/${world.seasonId}/feed`);
    await expect(
      watcher.getByTestId(/^feed-item-/).filter({ hasText: 'IRL-пруф отправлен' }),
    ).toHaveCount(1, live);
    await openAdmin(moderator, world, 'irlproofs');
    const proof = moderator
      .getByTestId('irl-proof-queue')
      .getByRole('article')
      .filter({ hasText: 'Караоке' });
    await expect(proof.getByRole('heading', { name: 'Караоке' })).toBeVisible(live);
    await expect(proof).toContainText(player.name);
    await expect(proof.getByRole('img')).toHaveAttribute('src', file.thumbnailUrl);
    await expect(proof.getByRole('link', { name: /открыть видео/i })).toHaveAttribute(
      'href',
      video,
    );
    await moderator.evaluate(() => document.fonts.ready);
    await moderator.screenshot({
      path: shot(`s5-23-admin-irl-queue-${info.project.name}.png`),
    });
    await proof.getByRole('button', { name: 'Отклонить', exact: true }).click();
    const rejection = moderator.getByRole('alertdialog');
    await rejection.getByRole('textbox', { name: 'Комментарий' }).fill('Добавь голос в записи');
    await moderator.screenshot({ path: shot(`s5-23-admin-irl-reject-${info.project.name}.png`) });
    await rejection.getByRole('button', { name: 'Отклонить', exact: true }).click();
    await expect(task.getByText('Пруф отклонён', { exact: true })).toBeVisible(live);
    await expect(task).toContainText('Добавь голос в записи');
    await expect(task.getByRole('textbox', { name: /ссылка на видео/i })).toHaveValue(video);
    await expect(task.getByRole('img')).toHaveCount(1);
    await expect(
      watcher.getByTestId(/^feed-item-/).filter({ hasText: 'IRL-пруф отклонён' }),
    ).toHaveCount(1, live);
    await expect(watcher.getByText('Добавь голос в записи', { exact: true })).toHaveCount(0);
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
    await expect(proof.getByRole('link', { name: /открыть видео/i })).toHaveAttribute(
      'href',
      corrected,
      live,
    );
    const [approvalResponse] = await Promise.all([
      moderator.waitForResponse(
        (response) =>
          response.request().method() === 'POST' &&
          new URL(response.url()).pathname === `${adminPath}/effects/${effect.id}/irl-proof/review`,
      ),
      proof.getByRole('button', { name: 'Одобрить', exact: true }).click(),
    ]);
    expect(approvalResponse.ok()).toBe(true);
    const approval = (await approvalResponse.json()) as Schemas['CommandResponse'];
    expect(approval.events.some((event) => event.type === 'irl-proof-reviewed')).toBe(true);
    await expect(moderator.getByText('Нет IRL-пруфов на проверке')).toBeVisible(live);
    await expect(task).toHaveCount(0, live);
    expect((await seasonOf(owner, world.seasonId)).me?.manualEffects).toHaveLength(0);
    await expect(
      watcher.getByTestId(/^feed-item-/).filter({ hasText: 'IRL-пруф одобрен' }),
    ).toHaveCount(1, live);
    await expect(watcher.locator(`a[href^="${video}"]`)).toHaveCount(0);
    await expect(watcher.locator(`img[src*="${file.id}"]`)).toHaveCount(0);
    await watcher.screenshot({
      path: shot(`s5-21-irl-feed-${info.project.name}.png`),
      fullPage: true,
    });
    expect(externalRequests).toEqual([]);
  } finally {
    if (!page.isClosed()) await page.context().close();
    if (!watcher.isClosed()) await watcher.context().close();
    if (!moderator.isClosed()) await moderator.context().close();
    await owner.dispose();
    await other.dispose();
    await admin.dispose();
  }
});
