import { expect, test, type Page } from '@playwright/test';
import { PNG } from 'pngjs';

// The map and the design system, in every engine and size of playwright.visual.config.ts.
// Besides the screenshots against baselines, the map is checked pixel by pixel for the bug «thick black bands along
// the road and over a zone» (reported after G2): the road's middle must be the road's colour and a zone must stay its
// own colour, whatever the baseline says.

type Sample = { x: number; y: number };
type Samples = { roads: Sample[][]; zones: { id: string; points: Sample[] }[] };

async function openStyleguide(page: Page, anchor: string) {
  await page.goto(`/styleguide#${anchor}`);
  await page.locator(`#${anchor}`).waitFor();
  await page.evaluate(() => document.fonts.ready);
}

/** Points on the road's middle and inside each zone, away from cells, stickers and the road's outline, in CSS
 *  pixels from the map's top left corner */
async function samplePoints(page: Page): Promise<Samples> {
  return page
    .locator('#map svg[role="application"]')
    .first()
    .evaluate((node) => {
      const svg = node as SVGSVGElement;
      const matrix = svg.getScreenCTM();
      const frame = svg.getBoundingClientRect();
      if (!matrix) return { roads: [], zones: [] };
      const toScreen = (x: number, y: number) => {
        const p = new DOMPoint(x, y).matrixTransform(matrix);
        return { x: p.x - frame.left, y: p.y - frame.top };
      };
      const cells = [...svg.querySelectorAll<SVGGElement>('[data-cell]')].map((g) => {
        const b = g.getBBox();
        return { x: b.x + b.width / 2, y: b.y + b.height / 2, r: Math.max(b.width, b.height) / 2 };
      });
      // Stickers stand over cells and the road: their screen boxes are left out
      const stickers = [...svg.querySelectorAll<SVGGElement>('[data-player]')].map((g) =>
        g.getBoundingClientRect(),
      );
      const underSticker = (x: number, y: number) => {
        const p = toScreen(x, y);
        return stickers.some(
          (r) =>
            p.x + frame.left > r.left - 2 &&
            p.x + frame.left < r.right + 2 &&
            p.y + frame.top > r.top - 2 &&
            p.y + frame.top < r.bottom + 2,
        );
      };
      const clear = (x: number, y: number, gap: number) =>
        cells.every((c) => Math.hypot(c.x - x, c.y - y) > c.r + gap);
      const roadPoints: { x: number; y: number }[] = [];
      const roads = [...svg.querySelectorAll<SVGPathElement>('[data-road]')].map((road) => {
        const out: Sample[] = [];
        for (let at = 0; at < road.getTotalLength(); at += 3) {
          const p = road.getPointAtLength(at);
          roadPoints.push({ x: p.x, y: p.y });
          if (clear(p.x, p.y, 4) && !underSticker(p.x, p.y)) out.push(toScreen(p.x, p.y));
        }
        return out;
      });
      const zones = [...svg.querySelectorAll<SVGPathElement>('[data-zone]')].map((zone) => {
        const b = zone.getBBox();
        const points: Sample[] = [];
        for (let x = b.x + 10; x < b.x + b.width; x += 20)
          for (let y = b.y + 10; y < b.y + b.height; y += 20) {
            if (!zone.isPointInFill(new DOMPoint(x, y))) continue;
            if (!clear(x, y, 12) || underSticker(x, y)) continue;
            if (roadPoints.some((r) => Math.hypot(r.x - x, r.y - y) < 20)) continue;
            points.push(toScreen(x, y));
          }
        return { id: zone.dataset['zone'] ?? '', points };
      });
      return { roads, zones };
    });
}

function luminance(png: PNG, scale: number, p: Sample) {
  const x = Math.round(p.x * scale);
  const y = Math.round(p.y * scale);
  if (x < 0 || y < 0 || x >= png.width || y >= png.height) return null;
  const i = (png.width * y + x) * 4;
  const [r, g, b] = [png.data[i] ?? 0, png.data[i + 1] ?? 0, png.data[i + 2] ?? 0];
  return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
}

test('the map has no dark bands: the road is light along its middle, every zone keeps its colour', async ({
  page,
}) => {
  await openStyleguide(page, 'map');
  const map = page.locator('#map svg[role="application"]').first();
  await map.scrollIntoViewIfNeeded();
  // The whole board in view, on a phone too (a tall frame opens around my token)
  const zoomOut = page.locator('#map').getByRole('button', { name: 'Отдалить' }).first();
  for (let i = 0; i < 6; i++) await zoomOut.click();
  const samples = await samplePoints(page);
  const box = await map.boundingBox();
  expect(box).not.toBeNull();
  const shot = PNG.sync.read(await map.screenshot());
  const scale = shot.width / (box?.width ?? 1);

  const road = samples.roads
    .flat()
    .map((p) => luminance(shot, scale, p))
    .filter((l): l is number => l !== null);
  expect(road.length).toBeGreaterThan(40);
  const darkOnRoad = road.filter((l) => l < 0.5).length / road.length;
  expect(darkOnRoad, 'share of dark pixels along the road').toBeLessThan(0.05);

  // Every zone of the demo board is checked: none may slip through for lack of sample points
  expect(samples.zones.length).toBe(6);
  for (const zone of samples.zones) {
    const light = zone.points
      .map((p) => luminance(shot, scale, p))
      .filter((l): l is number => l !== null);
    expect(light.length, `sample points in the zone «${zone.id}»`).toBeGreaterThanOrEqual(10);
    const dark = light.filter((l) => l < 0.2).length / light.length;
    expect(dark, `share of near-black pixels in the zone «${zone.id}»`).toBeLessThan(0.2);
  }
});

test('the map looks as approved', async ({ page }) => {
  await openStyleguide(page, 'map');
  const map = page.locator('#map svg[role="application"]').first();
  await map.scrollIntoViewIfNeeded();
  await expect(map).toHaveScreenshot('map.png');
});

for (const section of ['buttons', 'fields', 'states', 'progress', 'run']) {
  test(`the styleguide section «${section}» looks as approved`, async ({ page }) => {
    await openStyleguide(page, section);
    await expect(page.locator(`#${section}`)).toHaveScreenshot(`${section}.png`);
  });
}
