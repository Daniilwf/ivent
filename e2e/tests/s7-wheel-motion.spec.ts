import { expect, test } from '@playwright/test';
import { apiOf, seasonOf, setUpSeason, signIn, signInHere } from '../support/world.ts';

test.use({
  reducedMotion: 'no-preference',
  contextOptions: { reducedMotion: 'no-preference' },
});

test('the server offer appears while the wheel renderer is still downloading', async ({
  browser,
}) => {
  const world = await setUpSeason({ players: ['Лиса'] });
  const [player] = world.players;
  if (!player) throw new Error('A player is required.');
  const owner = await apiOf(player);
  const page = await signIn(browser, player);
  let releaseMotion: (() => void) | undefined;
  const motion = new Promise<void>((resolve) => {
    releaseMotion = resolve;
  });
  let requestedMotion = false;
  try {
    await page.clock.install();
    await page.route('**/assets/motionFeatures-*.js', async (route) => {
      requestedMotion = true;
      await motion;
      await route.continue();
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    expect(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(
      false,
    );
    const roll = page.getByTestId('roll');
    await expect(roll).toBeVisible();
    // Exercise the exact animation deadline even when parallel builds delay the browser process.
    await page.clock.pauseAt(await page.evaluate(() => Date.now() + 500));
    await roll.click();
    await expect(page.getByTestId('wheel')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Показать результат' })).toBeVisible();
    await page.clock.runFor(3500);
    await expect(page.getByTestId('offer')).toBeVisible();
    expect(requestedMotion).toBe(true);

    const offer = (await seasonOf(owner, world.seasonId)).me?.offer;
    if (!offer) throw new Error('The server did not record an offer.');
    await expect(page.locator('#roll-result-title')).toHaveText(offer.title);
  } finally {
    releaseMotion?.();
    await page.unrouteAll({ behavior: 'wait' });
    await page.context().close();
    await owner.dispose();
    await world.adminApi.dispose();
  }
});

type WheelFrame = { at: number; angle: number; scroll: number };
type WheelWindow = Window & { wheelMotionFrames?: WheelFrame[] };

function observeWheel() {
  const frames: WheelFrame[] = [];
  (window as WheelWindow).wheelMotionFrames = frames;
  const sample = (at: number) => {
    const disc = document.querySelector('[data-wheel-disc]');
    if (disc) {
      const transform = getComputedStyle(disc).transform;
      const matrix = new DOMMatrixReadOnly(transform === 'none' ? undefined : transform);
      frames.push({
        at,
        angle: (Math.atan2(matrix.b, matrix.a) * 180) / Math.PI,
        scroll: window.scrollY,
      });
    }
    requestAnimationFrame(sample);
  };
  requestAnimationFrame(sample);
}

test('the wheel keeps the page still and turns at a readable speed', async ({ page }, info) => {
  const world = await setUpSeason({ players: ['Лиса'] });
  try {
    const player = world.players[0];
    if (!player) throw new Error('A player is required.');
    await page.goto('/');
    await signInHere(page, player);
    const roll = page.getByTestId('roll');
    await roll.scrollIntoViewIfNeeded();
    const box = await roll.boundingBox();
    if (!box) throw new Error('The roll button did not appear.');
    const before = await page.evaluate(() => window.scrollY);
    await page.evaluate(observeWheel);
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    const wheel = page.getByTestId('wheel');
    await expect(wheel).toBeInViewport({ ratio: 0.9 });
    if (info.project.name === 'desktop') {
      await expect(page.getByTestId('moment-status')).toHaveText('Крутится колесо жанров…');
      await expect(page.getByTestId('moment-status')).toHaveAttribute('role', 'status');
      await expect(page.getByRole('button', { name: 'Показать результат' })).toBeVisible();
    }
    await page.screenshot({ path: info.outputPath('wheel-turning.png'), scale: 'css' });
    const result = page.getByTestId('offer').or(page.getByTestId('choice'));
    await expect(result).toBeVisible();
    if (info.project.name === 'phone') {
      await expect(result).not.toHaveCSS('animation-name', 'none');
      await expect(result).toHaveCSS('animation-duration', '0.4s');
    }
    const frames = await page.evaluate(() => (window as WheelWindow).wheelMotionFrames ?? []);
    expect(frames.length).toBeGreaterThan(10);
    const first = frames[0];
    const last = frames.at(-1);
    if (!first || !last) throw new Error('No rendered frames of the wheel.');
    expect(last.at - first.at).toBeGreaterThanOrEqual(2600);
    const scrollChange = Math.max(...frames.map((frame) => Math.abs(frame.scroll - before)));
    expect(scrollChange).toBeLessThan(3);
    expect(Math.abs((await page.evaluate(() => window.scrollY)) - before)).toBeLessThan(3);

    let rotation = 0;
    const speeds: number[] = [];
    const positions: { at: number; rotation: number }[] = [];
    for (const [index, frame] of frames.entries()) {
      const previous = frames[index - 1];
      if (previous) rotation += ((frame.angle - previous.angle + 540) % 360) - 180;
      // Average across several painted frames to avoid attributing an animation tick to the wrong rAF interval.
      const start = positions.findLast((point) => frame.at - point.at >= 140);
      if (start) speeds.push((Math.abs(rotation - start.rotation) * 1000) / (frame.at - start.at));
      positions.push({ at: frame.at, rotation });
    }
    expect(Math.abs(rotation)).toBeGreaterThan(360);
    // The token tests enforce the 720°/s curve cap exactly. Browser sampling allows a single frame of clock skew.
    const peak = Math.max(...speeds);
    expect(peak).toBeLessThanOrEqual(800);
    await info.attach('wheel-motion.json', {
      body: JSON.stringify({
        durationMs: last.at - first.at,
        peakDegreesPerSecond: peak,
        scrollChange,
      }),
      contentType: 'application/json',
    });
  } finally {
    await world.adminApi.dispose();
  }
});
