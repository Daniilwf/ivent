import { setTimeout as delay } from 'node:timers/promises';
import { expect, test, type Page, type Request } from '@playwright/test';
import { commandId, type Schemas } from '../support/api.ts';
import { apiOf, live, setUpSeason, signIn } from '../support/world.ts';

const discussion = /^Комментарии к записи/;
const empty = 'Комментариев пока нет.';
// The client throttles comment refreshes for 5 seconds after the previous refresh.
const observationMs = 5_250;

async function openThread(page: Page, id: string) {
  const entry = page.getByTestId(`feed-item-${id}`);
  const button = entry.getByRole('button', { name: discussion });
  await button.click();
  await expect(button).toHaveAttribute('aria-expanded', 'true');
  const thread = entry.getByRole('region', { name: discussion });
  await expect(thread).toBeVisible();
  return thread;
}

test('posted and moderated comments refresh only their own open feed discussion', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Игрок 1', 'Игрок 2', 'Игрок 3'], spectator: true });
  const [player] = world.players;
  const spectator = world.spectator;
  if (!player || !spectator) throw new Error('A player and a spectator are required.');

  const season = `/api/seasons/${world.seasonId}`;
  const feed = `/seasons/${world.seasonId}/feed`;
  try {
    const { version, ruleset } = await world.adminApi.get<Schemas['RulesView']>(`${season}/rules`);
    await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
      expectedVersion: version,
      ruleset: { ...ruleset, features: { ...ruleset.features, comments: true } },
    });
    const ids = await Promise.all(
      world.players.map(async (account) => {
        const api = await apiOf(account);
        const id = commandId();
        try {
          await api.post(`${season}/roll`, { commandId: id });
          return id;
        } finally {
          await api.dispose();
        }
      }),
    );
    const [targetId] = ids;
    if (!targetId) throw new Error('The target feed entry is required.');

    const [reader, author, admin] = await Promise.all([
      signIn(browser, spectator),
      signIn(browser, player),
      signIn(browser, world.admin),
    ]);
    try {
      await Promise.all([reader.goto(feed), author.goto(feed), admin.goto(feed)]);
      expect(reader.viewportSize()).toEqual(
        info.project.name === 'phone' ? { width: 390, height: 844 } : { width: 1440, height: 900 },
      );

      const targetPath = `${season}/feed/${targetId}/comments`;
      const paths = new Set(ids.map((id) => `${season}/feed/${id}/comments`));
      const reads: string[] = [];
      const settled = new Map<string, number>();
      function commentRead(request: Request) {
        const path = new URL(request.url()).pathname;
        return request.method() === 'GET' && paths.has(path) ? path : undefined;
      }
      reader.on('request', (request) => {
        const path = commentRead(request);
        if (path) reads.push(path);
      });
      function settle(request: Request) {
        const path = commentRead(request);
        if (path) settled.set(path, (settled.get(path) ?? 0) + 1);
      }
      reader.on('requestfinished', settle);
      reader.on('requestfailed', settle);

      const threads = [];
      for (const id of ids) {
        const thread = await openThread(reader, id);
        await expect(thread.getByText(empty, { exact: true })).toBeVisible();
        threads.push(thread);
      }
      const [target, ...neighbors] = threads;
      if (!target) throw new Error('The target discussion must be open.');
      // The passive reader fetches each discussion on open and refreshes it after the initial hub join.
      await expect
        .poll(() => [...paths].every((path) => (settled.get(path) ?? 0) >= 2), live)
        .toBe(true);

      async function checkOneRead(
        action: () => Promise<void>,
        updated: () => Promise<void>,
        label: string,
      ) {
        const start = reads.length;
        await action();
        // The target may render before an erroneous throttled GET to a neighbor starts.
        await Promise.all([updated(), delay(observationMs)]);
        const observed = reads.slice(start);
        await info.attach(label, {
          body: JSON.stringify({ expected: [targetPath], observed }, null, 2),
          contentType: 'application/json',
        });
        expect(observed).toEqual([targetPath]);
        for (const thread of neighbors)
          await expect(thread.getByText(empty, { exact: true })).toBeVisible();
      }

      const source = await openThread(author, targetId);
      const text = 'Каждое обсуждение обновляется отдельно.';
      await source.getByRole('textbox', { name: 'Написать комментарий' }).fill(text);
      await checkOneRead(
        () => source.getByRole('button', { name: 'Отправить', exact: true }).click(),
        () => expect(target.getByText(text, { exact: true })).toBeVisible(live),
        'GET after posting',
      );

      const moderated = await openThread(admin, targetId);
      await expect(moderated.getByText(text, { exact: true })).toBeVisible();
      await moderated.getByRole('button', { name: 'Скрыть', exact: true }).click();
      const confirm = admin.getByRole('alertdialog', { name: 'Скрыть комментарий?' });
      await checkOneRead(
        () => confirm.getByRole('button', { name: 'Скрыть', exact: true }).click(),
        () => expect(target.getByText(empty, { exact: true })).toBeVisible(live),
        'GET after hiding',
      );
      await expect(target.getByText(text, { exact: true })).toHaveCount(0);
    } finally {
      await Promise.all([reader, author, admin].map((page) => page.context().close()));
    }
  } finally {
    await world.adminApi.dispose();
  }
});
