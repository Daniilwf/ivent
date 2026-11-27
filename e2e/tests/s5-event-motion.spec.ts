import { expect, test, type Page, type TestInfo } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  cellOf,
  fillCompletionHere,
  live,
  rollAndStartHere,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

// Unlike the ordinary E2E suite, these scenarios must exercise real motion. signIn creates a separate context:
// both Playwright's option and its contextOptions are explicit, and matchMedia is checked on that actual page.
test.use({
  reducedMotion: 'no-preference',
  contextOptions: { reducedMotion: 'no-preference' },
});

const eventName = 'Тихая награда';
const historyLabel = `Последний ивент: «${eventName}»`;

type MomentSample = {
  at: number;
  dice: boolean;
  moving: boolean;
  busy: boolean;
  drawing: boolean;
  announcement: string;
};
type VisualStyle = { element: number; transform: string; opacity: string };
type PhysicalFrame = {
  at: number;
  dice: VisualStyle[];
  token: { x: number; y: number } | null;
  card: VisualStyle[];
};
type MotionTrace = {
  samples: MomentSample[];
  drawStarts: { at: number; busy: boolean }[];
  announcements: { at: number; text: string; busy: boolean }[];
  physicalFrames: PhysicalFrame[];
};
type MotionWindow = Window & { __eventMotion?: MotionTrace };

/** Observe existing public DOM states, without substituting animations, clocks or server responses. */
function observeMotion() {
  const host = window as MotionWindow;
  if (host.__eventMotion) return;
  const trace: MotionTrace = { samples: [], drawStarts: [], announcements: [], physicalFrames: [] };
  host.__eventMotion = trace;
  let previous = '';
  let previousAnnouncement = '';
  let activeDraws = new Set<Element>();
  const elementIds = new WeakMap<Element, number>();
  let nextElementId = 1;
  let framePending = false;
  const styles = (scope: Element | null): VisualStyle[] => {
    if (!scope) return [];
    // A public die/card may animate its root or a child. Observe visual properties of that bounded subtree only;
    // the same element must change between painted frames, so replacing text or adding a node cannot fake motion.
    const candidates = [scope, ...scope.querySelectorAll('*')].slice(0, 64);
    for (const animation of scope.getAnimations({ subtree: true })) {
      const effect = animation.effect;
      if (
        effect instanceof KeyframeEffect &&
        effect.target instanceof Element &&
        !candidates.includes(effect.target)
      )
        candidates.push(effect.target);
    }
    return candidates.map((element) => {
      let id = elementIds.get(element);
      if (id === undefined) {
        id = nextElementId++;
        elementIds.set(element, id);
      }
      const computed = getComputedStyle(element);
      return { element: id, transform: computed.transform, opacity: computed.opacity };
    });
  };
  const physicalFrame = () => {
    const die = document.querySelector('[data-testid="dice"] [data-die]');
    const map = document.querySelector(
      '[data-testid="dice"] [role="application"], [data-testid="walk"] [role="application"]',
    );
    const mover = map?.querySelector('[data-player]');
    let token: PhysicalFrame['token'] = null;
    if (mover instanceof SVGGraphicsElement && mover.ownerSVGElement) {
      const screen = mover.getScreenCTM();
      const camera = mover.ownerSVGElement.getScreenCTM();
      if (screen && camera) {
        // Express the token in the root SVG's coordinates. Page scrolling, zoom and camera movement cancel out.
        const position = camera.inverse().multiply(screen);
        token = { x: position.e, y: position.f };
      }
    }
    const card = document.querySelector('[data-testid="drawn-event"][data-drawing="true"]');
    if (die || map || card) {
      trace.physicalFrames.push({
        at: performance.now(),
        dice: styles(die),
        token,
        card: styles(card),
      });
      requestAnimationFrame(physicalFrame);
    } else {
      framePending = false;
    }
  };
  const sample = () => {
    const dice = Boolean(document.querySelector('[data-testid="dice"] [data-die]'));
    const moving = Boolean(
      document.querySelector(
        '[data-testid="dice"] [role="application"], [data-testid="walk"] [role="application"]',
      ),
    );
    const busy = Boolean(document.querySelector('[data-testid="dice"], [data-testid="walk"]'));
    const draws = new Set(
      document.querySelectorAll('[data-testid="drawn-event"][data-drawing="true"]'),
    );
    const announcement =
      document.querySelector('[data-testid="draw-announce"]')?.textContent.trim() ?? '';
    const at = performance.now();
    for (const draw of draws) {
      if (!activeDraws.has(draw)) trace.drawStarts.push({ at, busy });
    }
    activeDraws = draws;
    if (announcement && announcement !== previousAnnouncement)
      trace.announcements.push({ at, text: announcement, busy });
    previousAnnouncement = announcement;
    const state = { dice, moving, busy, drawing: draws.size > 0, announcement };
    const key = JSON.stringify(state);
    if (key !== previous) {
      trace.samples.push({ at, ...state });
      previous = key;
    }
    if ((busy || draws.size > 0) && !framePending) {
      framePending = true;
      requestAnimationFrame(physicalFrame);
    }
  };
  new MutationObserver(sample).observe(document, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ['data-drawing', 'data-testid', 'style', 'transform'],
  });
  sample();
}

/** At least three distinct painted values on one actual element, without requiring a particular effect or duration. */
function hasVisualMotion(trace: MotionTrace, phase: 'dice' | 'card') {
  const values = new Map<number, Set<string>>();
  for (const frame of trace.physicalFrames) {
    for (const style of frame[phase]) {
      const seen = values.get(style.element) ?? new Set<string>();
      seen.add(`${style.transform}|${style.opacity}`);
      values.set(style.element, seen);
    }
  }
  return [...values.values()].some((seen) => seen.size >= 3);
}

async function motionTrace(page: Page): Promise<MotionTrace> {
  return page.evaluate(() => {
    const trace = (window as MotionWindow).__eventMotion;
    if (!trace) throw new Error('The motion observer was not installed');
    return trace;
  });
}

async function attachTrace(page: Page, info: TestInfo, name: string) {
  await info.attach(name, {
    body: JSON.stringify(
      await page.evaluate(
        () => (window as MotionWindow).__eventMotion ?? { error: 'The observer was not installed' },
      ),
      null,
      2,
    ),
    contentType: 'application/json',
  });
}

/** Three normal dice stop on e3…e12; the event only gives coins and cannot add another movement or throw. */
function eventMap(): Schemas['MapGraphView'] {
  const ids = [
    'start',
    'c1',
    'c2',
    ...Array.from({ length: 10 }, (_, i) => `e${String(i + 3)}`),
    'finish',
  ];
  const cells: Schemas['CellView'][] = ids.map((id, i) => ({
    id,
    type:
      id === 'start'
        ? 'start'
        : id === 'finish'
          ? 'finish'
          : id.startsWith('e')
            ? 'event'
            : 'empty',
    ...(id.startsWith('e') ? { deck: 'good' as const } : {}),
    x: i * 110,
    y: 200,
  }));
  const edges: Schemas['EdgeView'][] = ids.slice(1).map((to, i) => ({
    from: ids[i] ?? 'start',
    to,
    isDefaultForward: true,
    isPrimaryBackward: true,
  }));
  return { cells, edges, zones: [] };
}

async function motionSeason() {
  const world = await setUpSeason({ players: ['Последовательный'], start: false });
  const admin = world.adminApi;
  const path = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const rules = await admin.get<Schemas['RulesView']>(`${path}/rules`);
  await admin.put(`${adminPath}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: { ...rules.ruleset.features, mapMode: 'graph', items: true, events: true },
      reward: {
        ...rules.ruleset.reward,
        diceCount: { ...rules.ruleset.reward.diceCount, min: 3, max: 3 },
      },
      events: { ...rules.ruleset.events, defaultDeck: 'good' },
    },
  });
  await admin.post(`${adminPath}/map/publish`, {
    map: eventMap(),
    comment: 'Порядок главных моментов',
  });
  await admin.post(`/api/test/seasons/${world.seasonId}/content`, {
    objects: [
      JSON.stringify({
        id: 'quiet-reward',
        kind: 'event',
        name: eventName,
        description: 'Получи одну монетку. Двигаться ещё раз не нужно.',
        rarity: 'common',
        price: null,
        hostile: false,
        effect: {
          target: { selector: 'self' },
          actions: [{ type: 'changeResource', resource: 'coins', amount: 1 }],
        },
      }),
    ],
    wheels: [
      JSON.stringify({
        id: 'good',
        name: 'Тихая колода',
        deck: 'good',
        entries: [{ objectId: 'quiet-reward', weight: 1 }],
      }),
    ],
    comment: 'Один ивент без движения',
  });
  await admin.post(`${adminPath}/status`, { to: 'active' });
  return world;
}

for (const skip of [false, true]) {
  test(
    skip
      ? 'clicking through the dice and the fresh event reveals the same one server result'
      : 'real dice, token movement and the fresh event play in order without replay on refresh or history',
    async ({ browser }, info) => {
      const world = await motionSeason();
      const player = world.players[0];
      if (!player) throw new Error('The motion season has no player');
      const owner = await apiOf(player);
      let page: Page | undefined;
      try {
        page = await signIn(browser, player);
        const activePage = page;
        expect(
          await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches),
        ).toBe(false);
        await expect(page.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();
        await expect(page.getByTestId('drawn-event')).toHaveCount(0);
        await rollAndStartHere(page);
        await fillCompletionHere(page);
        await page.addInitScript(observeMotion);
        await page.evaluate(observeMotion);

        // completeHere deliberately waits until the whole moment ends; observe the actual click instead.
        await page.getByTestId('complete-submit').click();
        await expect(page.getByTestId('dice').locator('[data-die]')).toHaveCount(3);
        if (skip) {
          await page
            .getByTestId('dice')
            .getByRole('button', { name: 'Показать результат', exact: true })
            .click();
        } else {
          await expect(page.getByTestId('dice').getByRole('application')).toBeVisible();
        }
        await expect.poll(async () => (await motionTrace(activePage)).drawStarts.length).toBe(1);
        const card = page.getByTestId('drawn-event');
        if (skip) {
          await expect(card).toHaveAttribute('data-drawing', 'true');
          // An ordinary click can auto-wait for the moving card to settle; force bypasses only that stability wait.
          await card.click({ force: true });
        }
        await expect(card).not.toHaveAttribute('data-drawing');
        await expect(card).toContainText(eventName);
        await expect(page.getByTestId('last-dice')).toBeVisible();
        await expect(page.getByTestId('dice')).toHaveCount(0);

        const trace = await motionTrace(page);
        await attachTrace(page, info, 'completion-motion.json');
        const firstDice = trace.samples.findIndex((s) => s.dice);
        const firstMove = trace.samples.findIndex((s) => s.moving);
        const firstDraw = trace.samples.findIndex((s) => s.drawing);
        expect(firstDice).toBeGreaterThanOrEqual(0);
        if (!skip) {
          expect(firstMove).toBeGreaterThan(firstDice);
          expect(firstDraw).toBeGreaterThan(firstMove);
          expect(
            hasVisualMotion(trace, 'dice'),
            'A die must visibly move across painted frames',
          ).toBe(true);
          expect(
            hasVisualMotion(trace, 'card'),
            'The fresh card must visibly move or fade across painted frames',
          ).toBe(true);
          const tokenPositions = new Set(
            trace.physicalFrames.flatMap((frame) =>
              frame.token ? [`${frame.token.x.toFixed(3)},${frame.token.y.toFixed(3)}`] : [],
            ),
          );
          expect(
            tokenPositions.size,
            'The token must move in SVG coordinates, independently of the camera',
          ).toBeGreaterThanOrEqual(3);
        }
        expect(trace.drawStarts).toHaveLength(1);
        expect(trace.drawStarts.every((s) => !s.busy)).toBe(true);
        expect(trace.samples.filter((s) => s.drawing && s.busy)).toEqual([]);
        expect(trace.announcements).toHaveLength(1);
        expect(trace.announcements[0]?.text).toBe(`Вытянут хороший ивент: «${eventName}»`);
        expect(trace.announcements.every((s) => !s.busy)).toBe(true);
        await expect(page.getByTestId('draw-announce')).toHaveText(
          `Вытянут хороший ивент: «${eventName}»`,
        );

        const result = await seasonOf(owner, world.seasonId);
        const completed = result.me?.lastCompleted;
        const drawn = result.me?.lastEvent;
        if (!completed || !drawn) throw new Error('The completed run or drawn event is missing');
        expect(completed.dice).toHaveLength(3);
        const total = completed.dice.reduce((sum, die) => sum + die.value, 0);
        expect(total).toBeGreaterThanOrEqual(3);
        expect(total).toBeLessThanOrEqual(12);
        expect(drawn.objectId).toBe('quiet-reward');
        await expect.poll(() => cellOf(activePage, player.name)).toBe(`cell-e${String(total)}`);

        if (!skip) {
          const coins = result.me?.coins;
          if (coins == null) throw new Error('No own balance');
          await world.adminApi.post(
            `/api/admin/seasons/${world.seasonId}/players/${player.playerId}/adjust`,
            {
              coinsDelta: 1,
              comment: 'Реальное обновление по SignalR',
            },
          );
          await expect(page.getByTestId('coins')).toHaveText(
            new RegExp(`^${String(coins + 1)} `),
            live,
          );
          expect((await motionTrace(page)).drawStarts).toHaveLength(1);
          expect((await motionTrace(page)).announcements).toHaveLength(1);

          // A following accepted own roll folds this event; opening its text must not replay the draw.
          await page.getByTestId('roll').click();
          await expect(page.getByTestId('offer')).toBeVisible();
          await page.getByText(historyLabel, { exact: true }).click();
          await expect(card).toBeVisible();
          await expect(card).not.toHaveAttribute('data-drawing');
          await page.getByText(historyLabel, { exact: true }).click();
          await page.getByText(historyLabel, { exact: true }).click();
          expect((await motionTrace(page)).drawStarts).toHaveLength(1);
          await page.getByTestId('nav-feed').first().click();
          await expect(page.getByTestId('feed')).toContainText(`Ивент «${eventName}»`);
          await page.goBack();
          await expect(
            page.getByRole('heading', { level: 1, name: world.seasonName }),
          ).toBeVisible();
          const returnedHistory = page
            .getByText(historyLabel, { exact: true })
            .locator('xpath=ancestor::details[1]');
          await expect(returnedHistory).toBeVisible();
          // Native back navigation may restore an already open disclosure; reveal it without toggling it closed.
          if ((await returnedHistory.getAttribute('open')) === null)
            await page.getByText(historyLabel, { exact: true }).click();
          await expect(returnedHistory).toHaveAttribute('open', '');
          await expect(card).toBeVisible();
          expect((await motionTrace(page)).drawStarts).toHaveLength(1);
          expect((await motionTrace(page)).announcements).toHaveLength(1);
          expect((await seasonOf(owner, world.seasonId)).me?.lastEvent?.sequence).toBe(
            drawn.sequence,
          );
          await attachTrace(page, info, 'same-sequence-refresh-history.json');

          await page.reload();
          await expect(page.getByTestId('offer')).toBeVisible();
          await page.getByText(historyLabel, { exact: true }).click();
          await expect(page.getByTestId('drawn-event')).toBeVisible();
          await expect(page.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
          expect((await motionTrace(page)).drawStarts).toHaveLength(0);
          expect((await motionTrace(page)).announcements).toHaveLength(0);
          await attachTrace(page, info, 'reload-does-not-replay.json');
        }
      } finally {
        if (page && !page.isClosed()) {
          await attachTrace(page, info, 'final-motion-trace.json');
          await page.context().close();
        }
        await owner.dispose();
        await world.adminApi.dispose();
      }
    },
  );
}
