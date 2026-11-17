import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test, type TestInfo } from '@playwright/test';
import { Api, type Schemas } from '../../support/api.ts';
import { compressingProxy } from '../../support/compressing-proxy.ts';
import { demoSeasonId, speedPlayer } from '../../support/global-setup.ts';
import { baseURL, testPassword } from '../../support/world.ts';

// DESIGN.md's 2.5 s budget and TESTING.md scenario 13, with actual stage-5 information in the canonical demo.
// The original S13 keeps its core-readiness criterion on this same events-on fixture. A disabled mechanic or an empty overlay is a fixture failure,
// never a fast passing measurement: conditions, a waiting throw and an IRL task must exist before opening the page.
const budgetMs = 2_500;
const slow4g = {
  offline: false,
  latency: 150,
  downloadThroughput: (1.6 * 1024 * 1024) / 8,
  uploadThroughput: (750 * 1024) / 8,
};
const trialName = 'Испытание скорости';
const designShot = (name: string) =>
  join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'test-artifacts', 'design', name);

async function preparedDemo(info: TestInfo) {
  const player = await Api.signIn(baseURL(), speedPlayer.login, testPassword).catch(
    (error: unknown) => {
      throw new Error(
        `No demo player ${speedPlayer.login}: the E2E global setup must prepare its canonical demo overlay.`,
        { cause: error },
      );
    },
  );
  try {
    await player.get('/api/seasons/current');
    const view = await player.get<Schemas['SeasonView']>(`/api/seasons/${demoSeasonId}`);
    const me = view.me;
    await info.attach('stage5-speed-preflight.json', {
      body: JSON.stringify(
        {
          seasonId: view.id,
          playerCount: view.players.length,
          eventsEnabled: me?.eventsEnabled,
          waitingConditions: me?.waitingConditions,
          waitingEvents: me?.waitingEvents,
          manualEffects: me?.manualEffects.map((effect) => ({
            id: effect.id,
            objectId: effect.objectId,
            name: effect.name,
            irl: effect.irl,
          })),
          lastEvent: me?.lastEvent,
        },
        null,
        2,
      ),
      contentType: 'application/json',
    });
    expect(view.id).toBe(demoSeasonId);
    expect(view.name).toBe('Демо-сезон');
    expect(
      view.players.length,
      'The speed test needs the canonical demo, with at least 16 players',
    ).toBeGreaterThanOrEqual(16);
    expect(
      me?.eventsEnabled,
      'Stage-5 speed preflight: events must be enabled before measuring',
    ).toBe(true);
    if (!me) throw new Error('The speed player must have an own turn');
    expect(
      me.waitingConditions?.find((condition) => condition.objectId === 'speed-trial'),
    ).toMatchObject({
      name: trialName,
      difficultyAtLeast: 'hard',
    });
    expect(me.waitingEvents?.find((event) => event.objectId === 'speed-trial')).toMatchObject({
      name: trialName,
      waitingForThrow: true,
    });
    const karaoke = me.manualEffects.find((effect) => effect.objectId === 'karaoke');
    expect(karaoke).toMatchObject({ name: 'Караоке', irl: true });
    expect(me.lastEvent).toMatchObject({ objectId: 'karaoke', name: 'Караоке' });
    if (!karaoke) throw new Error('The speed player must have a real pending IRL task');

    // FeedView.entries are events. Count actual distinct events in this season, not a global sequence watermark.
    // The public API accepts limit <= 100 and nextBefore as the next page's before cursor (FeedApiTests).
    const sequences = new Set<number>();
    let before: number | null = null;
    let pages = 0;
    while (sequences.size < 200 && pages < 3) {
      const query = new URLSearchParams({ limit: '100' });
      if (before !== null) query.set('before', String(before));
      const feed = await player.get<Schemas['FeedView']>(
        `/api/seasons/${demoSeasonId}/feed?${query.toString()}`,
      );
      for (const event of feed.entries) sequences.add(event.sequence);
      pages++;
      before = feed.nextBefore;
      if (before === null) break;
    }
    await info.attach('stage5-speed-demo-size.json', {
      body: JSON.stringify({ players: view.players.length, actualEvents: sequences.size, pages }),
      contentType: 'application/json',
    });
    expect(
      sequences.size,
      'The canonical demo must contain hundreds of actual season events',
    ).toBeGreaterThanOrEqual(200);

    // As in S13, the server is warm before timing a first browser visit. The browser cache is still empty.
    await player.get('/api/seasons/current');
    await player.get(`/api/seasons/${demoSeasonId}`);
    return { cookies: await player.cookies(), karaokeId: karaoke.id };
  } finally {
    await player.dispose();
  }
}

test('speed: events-on demo renders waiting conditions, a future throw and an IRL task within 2.5 s on Slow 4G', async ({
  browser,
}, info) => {
  const prepared = await preparedDemo(info);
  const context = await browser.newContext();
  let proxy: Awaited<ReturnType<typeof compressingProxy>> | undefined;
  try {
    await context.addCookies(prepared.cookies);
    const page = await context.newPage();
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Network.emulateNetworkConditions', slow4g);
    proxy = await compressingProxy(baseURL());
    await page.goto(proxy.url, { waitUntil: 'commit' });

    // Every frame tests the actual laid-out content, including the new information. Sections below the phone's
    // viewport still count as rendered, as in S13; hidden disclosures, absent text and unloaded forms do not.
    const shown = await page.waitForFunction(
      ({ karaokeId, trial }) => {
        const rendered = (element: Element | null | undefined) =>
          element != null &&
          element.getClientRects().length > 0 &&
          element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true });
        const text = (element: Element) => element.textContent.trim().replace(/\s+/g, ' ');
        const exactText = (scope: Element | null, expected: string) =>
          scope !== null &&
          [...scope.querySelectorAll('span,p,h2,h3,h4,button')].some(
            (element) => text(element) === expected && rendered(element),
          );
        const section = (name: string) =>
          [...document.querySelectorAll('section[aria-labelledby]')].find((element) => {
            const label = element.getAttribute('aria-labelledby');
            return label !== null && document.getElementById(label)?.textContent === name;
          }) ?? null;
        const conditions = section('Условия следующей игры');
        const events = section('Накопленные ивенты');
        const irl = document.querySelector(`[data-testid="manual-effect-${karaokeId}"]`);
        const map = document.querySelector('svg[role="application"]');
        const ready =
          document.querySelector('h1')?.textContent === 'Демо-сезон' &&
          rendered(document.querySelector('h1')) &&
          rendered(document.querySelector('[data-testid="turn"]')) &&
          rendered(map) &&
          map?.querySelector('[data-player]') !== null &&
          exactText(conditions, trial) &&
          exactText(conditions, 'Не ниже: Сложная') &&
          exactText(events, trial) &&
          exactText(events, 'Ждёт броска') &&
          rendered(irl) &&
          exactText(irl, 'Караоке') &&
          exactText(irl, 'Отправить пруф');
        return ready ? performance.now() : null;
      },
      { karaokeId: prepared.karaokeId, trial: trialName },
      { polling: 'raf', timeout: 20_000 },
    );
    const elapsed = Math.round(Number(await shown.jsonValue()));
    const parts = await page.evaluate((shownAt) => {
      const done = (part: RegExp) =>
        Math.round(
          Math.max(
            0,
            ...performance
              .getEntriesByType('resource')
              .filter(
                (entry) =>
                  part.test(new URL(entry.name).pathname) &&
                  entry.startTime + entry.duration <= shownAt,
              )
              .map((entry) => entry.startTime + entry.duration),
          ),
        );
      return `scripts ${String(done(/\.js$/))} ms, season ${String(done(/^\/api\/seasons\/[0-9a-f-]{36}$/))} ms`;
    }, elapsed);
    await expect(page.getByTestId('turn')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Условия следующей игры' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Накопленные ивенты' })).toBeVisible();
    await expect(
      page
        .getByTestId(`manual-effect-${prepared.karaokeId}`)
        .getByRole('heading', { name: 'Караоке' }),
    ).toBeVisible();
    // The elapsed value is already fixed: design evidence and font settling never enter the speed measurement.
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({
      path: designShot(`s5-21-speed-${info.project.name}.png`),
      fullPage: true,
    });
    info.annotations.push({
      type: 'main content',
      description: `${String(elapsed)} ms (${parts}; events on, condition, waiting throw and IRL task rendered)`,
    });
    expect(elapsed, `stage-5 main content in ${String(elapsed)} ms (${parts})`).toBeLessThanOrEqual(
      budgetMs,
    );
  } finally {
    await context.close();
    await proxy?.close();
  }
});
