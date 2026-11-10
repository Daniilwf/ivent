import type { Page } from '@playwright/test';

// Fingers on the map of a phone (Chromium only: the touches go through the DevTools protocol, D-275).

/** The map's visible box in map units (its viewBox): a smaller width is closer, the centre moves with a drag */
export async function mapView(page: Page) {
  const box = (await page.locator('svg[role="application"]').first().getAttribute('viewBox')) ?? '';
  const [x = 0, y = 0, w = 0, h = 0] = box.split(' ').map(Number);
  return { width: w, centre: x + w / 2, middle: y + h / 2 };
}

/** The middle of the map's frame on the screen */
export async function mapMiddle(page: Page) {
  const map = page.locator('svg[role="application"]').first();
  await map.scrollIntoViewIfNeeded();
  const frame = await map.boundingBox();
  if (!frame) throw new Error('The map has no box.');
  return { x: frame.x + frame.width / 2, y: frame.y + frame.height / 2 };
}

/** Two fingers move apart around a point: the map comes closer */
export async function pinchOut(page: Page, at: { x: number; y: number }) {
  const cdp = await page.context().newCDPSession(page);
  const touch = (spread: number) => [
    { x: at.x - spread, y: at.y, id: 1 },
    { x: at.x + spread, y: at.y, id: 2 },
  ];
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: touch(20) });
  for (const spread of [40, 60, 80])
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: touch(spread) });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await cdp.detach();
}

/** One finger drags from a point by an offset */
export async function fingerDrag(
  page: Page,
  at: { x: number; y: number },
  by: { x: number; y: number },
) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Input.dispatchTouchEvent', {
    type: 'touchStart',
    touchPoints: [{ x: at.x, y: at.y, id: 3 }],
  });
  await cdp.send('Input.dispatchTouchEvent', {
    type: 'touchMove',
    touchPoints: [{ x: at.x + by.x, y: at.y + by.y, id: 3 }],
  });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await cdp.detach();
}
