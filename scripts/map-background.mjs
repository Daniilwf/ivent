// Draws a map background for a graph map (D-279): the demo season's (content/map.demo.json) and the styleguide's.
// The picture is ours, made from the map itself: meadow ground, a soft region under each zone's cells, trees, hills and
// a lake kept off the cells and the arrows, a camp at the start and a castle behind the finish. The SVG is the source;
// the PNG next to it is what the demo seed uploads, rendered by the Playwright browser of e2e/ (no other tool needed).
//
//   node scripts/map-background.mjs            — both pictures, SVG and PNG
//   node scripts/map-background.mjs --svg-only — the SVGs only (no browser)
import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

const ink = '#1e2030';
const palette = {
  ground: '#b7e38a',
  groundDeep: '#a6dd72',
  tuft: '#8fcf5e',
  forest: '#4fb07a',
  forestDeep: '#3f9466',
  trunk: '#7a5230',
  hill: '#c9c1b6',
  snow: '#ffffff',
  water: '#7cc6c0',
  waterDeep: '#5fb3ad',
  sand: '#ffd59e',
  city: '#ffb36b',
  castle: '#b9a7f2',
  gold: '#ffc43d',
  card: '#ffffff',
};

/** The zones' regions: the first zone of a map is a swamp, the second a sandy trail, then by turns */
const regions = [
  { fill: palette.water, deep: palette.waterDeep, prop: 'reeds' },
  { fill: palette.sand, deep: palette.city, prop: 'stones' },
  { fill: palette.forest, deep: palette.forestDeep, prop: 'tree' },
];

function seeded(seed) {
  let s = seed;
  return () => {
    s = (s + 0x6d2b79f5) | 0;
    let r = Math.imul(s ^ (s >>> 15), 1 | s);
    r = (r + Math.imul(r ^ (r >>> 7), 61 | r)) ^ r;
    return ((r ^ (r >>> 14)) >>> 0) / 4294967296;
  };
}

function segmentDistance(p, a, b) {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const length = dx * dx + dy * dy;
  const k =
    length === 0 ? 0 : Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / length));
  return Math.hypot(p.x - (a.x + k * dx), p.y - (a.y + k * dy));
}

const tree = (x, y, k, deep) =>
  `<g transform="translate(${x.toFixed(1)} ${y.toFixed(1)}) scale(${k.toFixed(2)})">` +
  `<rect x="-4" y="6" width="8" height="16" fill="${palette.trunk}" stroke="${ink}" stroke-width="3"/>` +
  `<circle r="20" fill="${deep ? palette.forestDeep : palette.forest}" stroke="${ink}" stroke-width="3.5"/>` +
  `<circle cx="-7" cy="-7" r="6" fill="${palette.groundDeep}" opacity="0.7"/></g>`;

const hill = (x, y, k) =>
  `<g transform="translate(${x.toFixed(1)} ${y.toFixed(1)}) scale(${k.toFixed(2)})">` +
  `<path d="M-46 26 L-6 -34 L40 26 Z" fill="${palette.hill}" stroke="${ink}" stroke-width="4" stroke-linejoin="round"/>` +
  `<path d="M-18 -8 L-6 -34 L8 -10 L1 -4 L-6 -12 Z" fill="${palette.snow}"/></g>`;

const tuft = (x, y) =>
  `<path d="M${(x - 7).toFixed(1)} ${y.toFixed(1)} q4 -10 7 -14 q2 8 2 14 q3 -8 8 -11 q-2 7 -2 11" fill="none" stroke="${palette.tuft}" stroke-width="3" stroke-linecap="round"/>`;

const reeds = (x, y) =>
  `<g transform="translate(${x.toFixed(1)} ${y.toFixed(1)})" stroke="${ink}" stroke-width="2.5" stroke-linecap="round" fill="none">` +
  `<path d="M-6 0 V-18 M0 0 V-24 M6 0 V-15"/><ellipse cx="0" cy="-25" rx="2.5" ry="5" fill="${ink}"/></g>`;

const stones = (x, y) =>
  `<g transform="translate(${x.toFixed(1)} ${y.toFixed(1)})" stroke="${ink}" stroke-width="2.5">` +
  `<ellipse rx="9" ry="6" fill="${palette.hill}"/><ellipse cx="12" cy="4" rx="6" ry="4" fill="${palette.hill}"/></g>`;

const castle = (x, y) =>
  `<g transform="translate(${x.toFixed(1)} ${y.toFixed(1)}) scale(1.5)" stroke="${ink}" stroke-width="3" stroke-linejoin="round">` +
  `<rect x="-22" y="-20" width="44" height="40" fill="${palette.castle}"/>` +
  `<rect x="-34" y="-38" width="16" height="58" fill="${palette.castle}"/><rect x="18" y="-38" width="16" height="58" fill="${palette.castle}"/>` +
  `<path d="M-34 -38 h4 v-6 h4 v6 h4 v-6 h4 v6" fill="none"/><path d="M18 -38 h4 v-6 h4 v6 h4 v-6 h4 v6" fill="none"/>` +
  `<path d="M-6 20 v-14 a6 6 0 0 1 12 0 v14" fill="${ink}"/><path d="M26 -38 V-62 L44 -55 L26 -48" fill="${palette.gold}"/></g>`;

const camp = (x, y) =>
  `<g transform="translate(${x.toFixed(1)} ${y.toFixed(1)})" stroke="${ink}" stroke-width="3" stroke-linejoin="round">` +
  `<path d="M-30 18 L0 -26 L30 18 Z" fill="${palette.city}"/><path d="M0 -26 L0 18" fill="none"/>` +
  `<path d="M0 -26 V-50 L18 -44 L0 -38" fill="${palette.gold}"/></g>`;

/**
 * The picture of a map: `cells` with places in the picture's pixels, `edges` between them, `zones` in their order.
 * Everything drawn keeps `clear` pixels off the cells and arrows, so the board's own cells stay readable over it.
 */
function picture({ width, height, cells, edges, zones, seed }) {
  const random = seeded(seed);
  const at = new Map(cells.map((c) => [c.id, c]));
  const segments = edges
    .filter((e) => at.has(e.from) && at.has(e.to))
    .map((e) => [at.get(e.from), at.get(e.to)]);
  const clearOf = (p, clear) =>
    cells.every((c) => Math.hypot(c.x - p.x, c.y - p.y) > clear) &&
    segments.every(([a, b]) => segmentDistance(p, a, b) > clear * 0.75);
  const parts = [];
  parts.push(`<rect width="${width}" height="${height}" fill="${palette.ground}"/>`);
  // Soft patches of deeper grass
  for (let i = 0; i < 14; i++) {
    const x = random() * width;
    const y = random() * height;
    parts.push(
      `<ellipse cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" rx="${(120 + random() * 160).toFixed(1)}" ry="${(60 + random() * 90).toFixed(1)}" fill="${palette.groundDeep}" opacity="0.55"/>`,
    );
  }

  // A region under each zone: the box round its cells, rounded off
  const insideRegion = [];
  zones.forEach((zone, i) => {
    const inside = cells.filter((c) => c.zone === zone.id);
    if (inside.length === 0) return;
    const region = regions[i % regions.length];
    const reach = 78;
    const x0 = Math.min(...inside.map((c) => c.x)) - reach;
    const x1 = Math.max(...inside.map((c) => c.x)) + reach;
    const y0 = Math.min(...inside.map((c) => c.y)) - reach;
    const y1 = Math.max(...inside.map((c) => c.y)) + reach;
    insideRegion.push({ x0, x1, y0, y1, region });
    parts.push(
      `<rect x="${x0}" y="${y0}" width="${x1 - x0}" height="${y1 - y0}" rx="${reach}" fill="${region.fill}" stroke="${ink}" stroke-width="5" stroke-dasharray="2 14" stroke-linecap="round"/>`,
    );
  });
  const regionAt = (p) =>
    insideRegion.find(
      (r) => p.x > r.x0 + 20 && p.x < r.x1 - 20 && p.y > r.y0 + 16 && p.y < r.y1 - 16,
    );
  const nearRegion = (p, pad) =>
    insideRegion.some(
      (r) => p.x > r.x0 - pad && p.x < r.x1 + pad && p.y > r.y0 - pad && p.y < r.y1 + pad,
    );

  // A lake where there is room
  for (let i = 0; i < 400; i++) {
    const p = { x: 140 + random() * (width - 280), y: 140 + random() * (height - 280) };
    if (clearOf(p, 190) && !nearRegion(p, 120)) {
      parts.push(
        `<ellipse cx="${p.x.toFixed(1)}" cy="${p.y.toFixed(1)}" rx="110" ry="62" fill="${palette.water}" stroke="${ink}" stroke-width="5"/>`,
        `<path d="M${(p.x - 50).toFixed(1)} ${(p.y - 8).toFixed(1)} q16 -10 32 0 M${(p.x + 6).toFixed(1)} ${(p.y + 18).toFixed(1)} q16 -10 32 0" fill="none" stroke="${palette.snow}" stroke-width="4" stroke-linecap="round"/>`,
      );
      break;
    }
  }

  // Scenery: hills and woods on the meadow, the zone's own props inside its region, tufts everywhere
  const placed = [];
  const free = (p, gap) => placed.every((q) => Math.hypot(q.x - p.x, q.y - p.y) > gap);
  for (let i = 0; i < 9000 && placed.length < Math.round((width * height) / 9000); i++) {
    const p = { x: 30 + random() * (width - 60), y: 30 + random() * (height - 60) };
    const region = regionAt(p);
    if (!clearOf(p, region ? 44 : 64) || !free(p, region ? 34 : 46)) continue;
    if (!region && nearRegion(p, 10)) continue;
    placed.push(p);
    if (region) {
      const prop = region.region.prop;
      parts.push(
        prop === 'reeds'
          ? reeds(p.x, p.y)
          : prop === 'stones'
            ? stones(p.x, p.y)
            : tree(p.x, p.y, 0.8, true),
      );
    } else if (random() < 0.12 && clearOf(p, 110)) {
      parts.push(hill(p.x, p.y, 0.9 + random() * 0.5));
    } else if (random() < 0.55) {
      parts.push(tree(p.x, p.y, 0.8 + random() * 0.5, random() > 0.6));
    } else {
      parts.push(tuft(p.x, p.y));
    }
  }

  const start = cells.find((c) => c.type === 'start');
  const finish = cells.find((c) => c.type === 'finish');
  if (start) parts.push(camp(Math.max(40, start.x - 6), Math.max(60, start.y - 74)));
  if (finish) parts.push(castle(Math.min(width - 70, finish.x + 6), Math.max(110, finish.y - 96)));
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">\n${parts.join('\n')}\n</svg>\n`;
}

/** The styleguide's map (web/src/styleguide/mapFixtures.ts): its runs of cells read from the source */
function styleguideMap() {
  const source = readFileSync(join(root, 'web/src/styleguide/mapFixtures.ts'), 'utf8');
  const cells = [];
  const edges = [];
  for (const run of source.matchAll(/run\((null|'([a-z0-9]+)'), \[([\s\S]*?)\]\);/g)) {
    let previous = run[2] ?? null;
    for (const spot of run[3].matchAll(/\['([a-z0-9]+)', (\d+), (\d+)(?:, \{([^}]*)\})?\]/g)) {
      const [, id, x, y, extra = ''] = spot;
      if (!cells.some((c) => c.id === id))
        cells.push({
          id,
          x: Number(x),
          y: Number(y),
          zone: /zone: '([a-z]+)'/.exec(extra)?.[1],
          type: /type: '([a-zA-Z]+)'/.exec(extra)?.[1] ?? 'empty',
        });
      if (previous) edges.push({ from: previous, to: id });
      previous = id;
    }
  }
  const zones = [...source.matchAll(/id: '([a-z]+)',\s*name:/g)].map((m) => ({ id: m[1] }));
  return { cells, edges, zones };
}

const demo = JSON.parse(readFileSync(join(root, 'content/map.demo.json'), 'utf8'));
const jobs = [
  {
    svg: 'content/map.demo.background.svg',
    png: 'content/map.demo.background.png',
    picture: picture({
      width: 2070,
      height: 1520,
      cells: demo.cells,
      edges: demo.edges,
      zones: demo.zones,
      seed: 20261111,
    }),
    width: 2070,
    height: 1520,
  },
  {
    svg: 'web/src/styleguide/mapBackground.svg',
    png: null,
    picture: picture({ width: 1240, height: 620, ...styleguideMap(), seed: 7 }),
  },
];
for (const job of jobs) writeFileSync(join(root, job.svg), job.picture);

if (!process.argv.includes('--svg-only')) {
  const require = createRequire(join(root, 'e2e/package.json'));
  const { chromium } = require('@playwright/test');
  const browser = await chromium.launch();
  try {
    for (const job of jobs.filter((j) => j.png)) {
      const page = await browser.newPage({ viewport: { width: job.width, height: job.height } });
      await page.setContent(`<html><body style="margin:0">${job.picture}</body></html>`);
      writeFileSync(
        join(root, job.png),
        await page.screenshot({ type: 'png', omitBackground: false }),
      );
      await page.close();
    }
  } finally {
    await browser.close();
  }
}
