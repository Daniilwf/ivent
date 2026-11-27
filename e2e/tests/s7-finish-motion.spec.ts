import { expect, test, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  fillCompletionHere,
  revealRollHere,
  rollAndStartHere,
  rollHere,
  setUpSeason,
  signInHere,
} from '../support/world.ts';

const confetti = '[data-testid="finish-confetti"]';
const motionBudget = 3_000;
const completionResult = { timeout: 8_500 };

type FinishTrace = {
  sawDice: boolean;
  sawMove: boolean;
  starts: { at: number; settled: boolean }[];
  endedAt: number | null;
  drawStartedAt: number | null;
  drawSettledAt: number | null;
  overlapped: boolean;
  rolledDuringDraw: boolean;
  finishPoint: { x: number; y: number } | null;
  firstPaint: { x: number; y: number }[];
  firstPaintView: { width: number; height: number; scroll: number } | null;
};
type FinishWindow = Window & { finishMotionTrace?: FinishTrace };

function observeFinish({
  selector,
  rollDuringDraw,
}: {
  selector: string;
  rollDuringDraw?: boolean;
}) {
  const trace: FinishTrace = {
    sawDice: false,
    sawMove: false,
    starts: [],
    endedAt: null,
    drawStartedAt: null,
    drawSettledAt: null,
    overlapped: false,
    rolledDuringDraw: false,
    finishPoint: null,
    firstPaint: [],
    firstPaintView: null,
  };
  (window as FinishWindow).finishMotionTrace = trace;
  const canvasPrototype = CanvasRenderingContext2D.prototype;
  const clearRect = Reflect.get(canvasPrototype, 'clearRect');
  const translate = Reflect.get(canvasPrototype, 'translate');
  let paintPasses = 0;
  let canvasBounds: DOMRect | undefined;
  canvasPrototype.clearRect = function (x, y, width, height) {
    if (this.canvas.matches(selector)) paintPasses++;
    clearRect.call(this, x, y, width, height);
  };
  canvasPrototype.translate = function (x, y) {
    if (paintPasses === 1 && this.canvas.matches(selector)) {
      if (trace.firstPaint.length === 0) {
        trace.firstPaintView = { width: innerWidth, height: innerHeight, scroll: scrollY };
        const finish = [...document.querySelectorAll<SVGTextElement>('g[data-cell] > text')].find(
          (label) => label.textContent.trim() === 'Финиш',
        );
        const transform = finish?.getScreenCTM();
        if (finish && transform) {
          const point = new DOMPoint(
            Number(finish.getAttribute('x')),
            Number(finish.getAttribute('y')),
          ).matrixTransform(transform);
          trace.finishPoint = { x: point.x, y: point.y };
        }
      }
      canvasBounds ??= this.canvas.getBoundingClientRect();
      trace.firstPaint.push({
        x: canvasBounds.x + (x * canvasBounds.width) / this.canvas.width,
        y: canvasBounds.y + (y * canvasBounds.height) / this.canvas.height,
      });
    }
    translate.call(this, x, y);
  };
  let previous: Element | null = null;
  const sample = () => {
    const dice = document.querySelector('[data-testid="dice"]');
    const walk = document.querySelector('[data-testid="walk"]');
    trace.sawDice ||= Boolean(dice?.querySelector('[data-die]'));
    trace.sawMove ||= Boolean(walk || dice?.querySelector('[role="application"]'));
    const canvas = document.querySelector(selector);
    const drawing = document.querySelector('[data-testid="drawn-event"][data-drawing="true"]');
    const at = performance.now();
    if (drawing) trace.drawStartedAt ??= at;
    else if (trace.drawStartedAt !== null) trace.drawSettledAt ??= at;
    trace.overlapped ||= Boolean(canvas && drawing);
    if (canvas && canvas !== previous) {
      trace.starts.push({ at, settled: !dice && !walk });
    }
    if (!canvas && previous) trace.endedAt = at;
    previous = canvas;
    if (rollDuringDraw && drawing && !trace.rolledDuringDraw) {
      const roll = document.querySelector<HTMLButtonElement>('[data-testid="roll"]');
      if (roll && !roll.disabled) {
        trace.rolledDuringDraw = true;
        roll.click();
      }
    }
  };
  new MutationObserver(sample).observe(document, {
    childList: true,
    subtree: true,
    attributes: true,
    attributeFilter: ['data-testid', 'data-drawing', 'disabled'],
  });
  sample();
}

async function finishTrace(page: Page) {
  return page.evaluate(() => {
    const trace = (window as FinishWindow).finishMotionTrace;
    if (!trace) throw new Error('The finish observer was not installed.');
    return trace;
  });
}

async function hasPaintedConfetti(page: Page) {
  return page.evaluate((selector) => {
    const canvas = document.querySelector<HTMLCanvasElement>(selector);
    if (!canvas || !canvas.width || !canvas.height) return false;
    const pixels = canvas.getContext('2d')?.getImageData(0, 0, canvas.width, canvas.height).data;
    return pixels?.some((value, index) => index % 4 === 3 && value > 0) ?? false;
  }, confetti);
}

for (const motion of ['no-preference', 'reduce'] as const) {
  test.describe(motion, () => {
    test.use({ reducedMotion: motion, contextOptions: { reducedMotion: motion } });

    test('own finish shows its result and respects the motion preference', async ({
      page,
    }, info) => {
      const world = await setUpSeason({ players: ['Вася'] });
      try {
        const vasya = world.players[0];
        if (!vasya) throw new Error('No player to finish.');
        await world.adminApi.post(`/api/test/seasons/${world.seasonId}/scenarios/finish-soon`, {
          player: vasya.login,
        });
        await page.addInitScript(observeFinish, { selector: confetti });
        await page.goto('/');
        await signInHere(page, vasya);
        expect(
          await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches),
        ).toBe(motion === 'reduce');
        await rollAndStartHere(page);
        await fillCompletionHere(page);

        await page.getByTestId('complete-submit').click();
        const finish = page.getByTestId('my-finish');
        const canvas = page.locator(confetti);
        await expect(finish).toContainText(
          'Первое место — твоё, пока предварительно',
          completionResult,
        );
        if (motion === 'no-preference') {
          await expect(canvas).toBeInViewport();
          await expect(canvas).toHaveAttribute('aria-hidden', 'true');
          await expect.poll(() => hasPaintedConfetti(page)).toBe(true);
          await page.screenshot({ path: info.outputPath('finish.png'), scale: 'css' });
          await expect(canvas).toHaveCount(0);

          const trace = await finishTrace(page);
          expect(trace.sawDice).toBe(true);
          expect(trace.sawMove).toBe(true);
          expect(trace.starts).toHaveLength(1);
          const start = trace.starts[0];
          if (!start || trace.endedAt === null) throw new Error('The celebration did not finish.');
          expect(start.settled).toBe(true);
          expect(trace.endedAt - start.at).toBeLessThanOrEqual(motionBudget);
          if (info.project.name === 'desktop') {
            const finishPoint = trace.finishPoint;
            if (!finishPoint) throw new Error('The painted finish cell was not found.');
            expect(trace.firstPaint.length).toBe(70);
            const centerX =
              trace.firstPaint.reduce((sum, particle) => sum + particle.x, 0) /
              trace.firstPaint.length;
            const centerY =
              trace.firstPaint.reduce((sum, particle) => sum + particle.y, 0) /
              trace.firstPaint.length;
            expect(Math.abs(centerX - finishPoint.x)).toBeLessThan(32);
            expect(centerY - finishPoint.y).toBeGreaterThanOrEqual(-4);
            expect(centerY - finishPoint.y).toBeLessThan(32);
          }
        } else {
          await expect(canvas).toHaveCount(0);
          expect((await finishTrace(page)).starts).toHaveLength(0);
        }
        await expect(finish.getByRole('heading', { name: 'Ты первый на финише!' })).toBeVisible();

        await page.reload();
        await expect(finish).toContainText('Первое место — твоё, пока предварительно');
        await expect(canvas).toHaveCount(0);
        expect((await finishTrace(page)).starts).toHaveLength(0);
      } finally {
        await info.attach('finish-motion.json', {
          body: JSON.stringify(
            await page.evaluate(() => (window as FinishWindow).finishMotionTrace),
          ),
          contentType: 'application/json',
        });
        await world.adminApi.dispose();
      }
    });
  });
}

test.describe('branch finish', () => {
  test.use({ reducedMotion: 'no-preference', contextOptions: { reducedMotion: 'no-preference' } });

  test('finishing after a branch celebrates once and a click still acts on the page', async ({
    page,
  }) => {
    const world = await setUpSeason({ players: ['Путник'], start: false });
    try {
      const traveler = world.players[0];
      if (!traveler) throw new Error('No player to finish.');
      const admin = world.adminApi;
      const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
      await admin.put(`/api/admin/seasons/${world.seasonId}/rules`, {
        expectedVersion: rules.version,
        ruleset: {
          ...rules.ruleset,
          features: { ...rules.ruleset.features, mapMode: 'graph' },
          reward: {
            ...rules.ruleset.reward,
            diceCount: { ...rules.ruleset.reward.diceCount, min: 3, max: 3 },
            dieByDifficulty: { ...rules.ruleset.reward.dieByDifficulty, normal: { sides: 2 } },
          },
        },
      });
      const map: Schemas['MapGraphView'] = {
        cells: [
          { id: 'start', type: 'start', x: 0, y: 160 },
          { id: 'fork', type: 'fork', x: 140, y: 160 },
          { id: 'a', type: 'empty', x: 280, y: 80 },
          { id: 'b', type: 'empty', x: 280, y: 240 },
          { id: 'finish', type: 'finish', x: 420, y: 160 },
        ],
        edges: [
          { from: 'start', to: 'fork', isDefaultForward: true, isPrimaryBackward: true },
          { from: 'fork', to: 'a', isDefaultForward: true, isPrimaryBackward: true },
          { from: 'fork', to: 'b', isDefaultForward: false, isPrimaryBackward: true },
          { from: 'a', to: 'finish', isDefaultForward: true, isPrimaryBackward: true },
          { from: 'b', to: 'finish', isDefaultForward: true, isPrimaryBackward: false },
        ],
        zones: [],
      };
      await admin.post(`/api/admin/seasons/${world.seasonId}/map/publish`, {
        map,
        comment: 'Развилка перед финишем',
      });
      await admin.post(`/api/admin/seasons/${world.seasonId}/status`, { to: 'active' });

      await page.addInitScript(observeFinish, { selector: confetti });
      await page.goto('/');
      await signInHere(page, traveler);
      await rollAndStartHere(page);
      await fillCompletionHere(page);
      await page.getByTestId('complete-submit').click();
      const branch = page.getByTestId('branch');
      await expect(branch).toBeVisible();
      expect((await finishTrace(page)).starts).toHaveLength(0);
      await branch.getByTestId('branch-b').click();

      const canvas = page.locator(confetti);
      await expect(canvas).toBeInViewport();
      await expect.poll(() => hasPaintedConfetti(page)).toBe(true);
      const trace = await finishTrace(page);
      expect(trace.starts).toHaveLength(1);
      expect(trace.starts[0]?.settled).toBe(true);

      await rollHere(page);
      await expect(canvas).toHaveCount(0);
      await expect(page.getByTestId('offer').or(page.getByTestId('choice'))).toBeVisible();
      await expect(page.getByTestId('my-finish')).toContainText(
        'Первое место — твоё, пока предварительно',
      );
      expect((await finishTrace(page)).starts).toHaveLength(1);
    } finally {
      await world.adminApi.dispose();
    }
  });
});

for (const motion of ['no-preference', 'reduce'] as const) {
  test.describe(motion, () => {
    test.use({ reducedMotion: motion, contextOptions: { reducedMotion: motion } });
    const routes =
      motion === 'reduce'
        ? (['history', 'choice'] as const)
        : (['history', 'choice', 'next roll'] as const);
    for (const route of routes) {
      const asksQuestion = route === 'choice';
      const description =
        route === 'next roll'
          ? 'the next roll cancels a celebration queued behind a finish event'
          : `finish event resolves ${asksQuestion ? 'in the answer form' : 'in history'} before celebrating`;
      test(description, async ({ page }) => {
        const world = await setUpSeason({ players: ['Вася'], start: false });
        const admin = world.adminApi;
        const seasonPath = `/api/seasons/${world.seasonId}`;
        const adminPath = `/api/admin/seasons/${world.seasonId}`;
        try {
          const player = world.players[0];
          if (!player) throw new Error('No player to finish.');
          const { ruleset, version } = await admin.get<Schemas['RulesView']>(`${seasonPath}/rules`);
          await admin.put(`${adminPath}/rules`, {
            expectedVersion: version,
            ruleset: {
              ...ruleset,
              features: { ...ruleset.features, items: true, events: true },
              reward: {
                ...ruleset.reward,
                dieByDifficulty: {
                  ...ruleset.reward.dieByDifficulty,
                  normal: { ...ruleset.reward.dieByDifficulty.normal, grantEvent: 'good' },
                },
              },
            },
          });
          const eventName = 'Ивент на финише';
          const target = { selector: 'self' };
          const coins = [{ type: 'changeResource', resource: 'coins', amount: 1 }];
          await admin.post(`/api/test/seasons/${world.seasonId}/content`, {
            objects: [
              JSON.stringify({
                id: 'finish-event',
                kind: 'event',
                name: eventName,
                description: 'Награда за прохождение.',
                rarity: 'common',
                price: null,
                hostile: false,
                effect: asksQuestion
                  ? {
                      target,
                      outcomes: {
                        choice: { prompt: 'Что берёшь?', options: { list: ['а', 'б'] } },
                        cases: [
                          { option: 'а', actions: coins },
                          { option: 'б', actions: coins },
                        ],
                      },
                    }
                  : { target, actions: coins },
              }),
            ],
            wheels: [
              JSON.stringify({
                id: 'good',
                name: 'Колода за сложность',
                deck: 'good',
                entries: [{ objectId: 'finish-event', weight: 1 }],
              }),
            ],
            comment: 'Ивент за сложность на клетке финиша',
          });
          await admin.post(`${adminPath}/status`, { to: 'active' });
          await admin.post(`/api/test/seasons/${world.seasonId}/scenarios/finish-soon`, {
            player: player.login,
          });

          await page.addInitScript(observeFinish, {
            selector: confetti,
            rollDuringDraw: route === 'next roll',
          });
          await page.goto('/');
          await signInHere(page, player);
          await rollAndStartHere(page);
          await fillCompletionHere(page);
          await page.getByTestId('complete-submit').click();

          const event = page.getByTestId('drawn-event');
          const finish = page.getByTestId('my-finish');
          const canvas = page.locator(confetti);
          await expect(event).toContainText(eventName, completionResult);
          if (asksQuestion) await expect(page.getByTestId('event-choice')).toContainText(eventName);
          if (route === 'next roll') {
            await expect(page.getByTestId('wheel')).toBeVisible();
            await revealRollHere(page);
            await expect(page.getByTestId('offer').or(page.getByTestId('choice'))).toBeVisible();
            await expect(finish).toContainText('Первое место — твоё, пока предварительно');
            const trace = await finishTrace(page);
            expect(trace.rolledDuringDraw).toBe(true);
            expect(trace.starts).toHaveLength(0);
            return;
          }
          if (motion === 'reduce') {
            await expect(finish).toContainText('Первое место — твоё, пока предварительно');
            await expect(event).not.toHaveAttribute('data-drawing');
            await expect(canvas).toHaveCount(0);
            expect((await finishTrace(page)).starts).toHaveLength(0);
            return;
          }

          if (asksQuestion) {
            await expect(event).toHaveAttribute('data-drawing', 'true');
            expect((await finishTrace(page)).starts).toHaveLength(0);
            await event.dispatchEvent('click');
          }
          await expect(canvas).toBeInViewport();
          await expect.poll(() => hasPaintedConfetti(page)).toBe(true);
          const trace = await finishTrace(page);
          expect(trace.overlapped).toBe(false);
          expect(trace.starts).toHaveLength(1);
          const start = trace.starts[0];
          if (!start || trace.drawStartedAt === null || trace.drawSettledAt === null)
            throw new Error('The draw must start and settle before confetti.');
          expect(start.settled).toBe(true);
          expect(start.at).toBeGreaterThanOrEqual(trace.drawSettledAt);
          await expect(event).not.toHaveAttribute('data-drawing');
          await expect(finish).toContainText('Первое место — твоё, пока предварительно');
        } finally {
          await admin.dispose();
        }
      });
    }
  });
}
