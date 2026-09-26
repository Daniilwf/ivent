// Screenshots of the design previews (G2) or of any pages, at the two sizes of docs/DESIGN.md: 390×844 and 1440×900.
//   node e2e/design-shots.mjs <base address> <out dir> <path>...
// A path may carry an anchor (design-preview.html#wheel); «!» at its end presses the page's main button first and
// waits for the moment to end, so the shot shows the result; «!1700» waits that many milliseconds instead, and
// SHOTS_MOTION=on keeps animations running, to catch a moment in the middle.
// Uses the bundled Chromium when installed, Edge otherwise (PW_CHANNEL=msedge); PW_BROWSER=firefox or webkit takes
// the shots in that engine, with its name in the file names.
import { chromium, firefox, webkit } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const [base, out, ...paths] = process.argv.slice(2);
if (!base || !out || paths.length === 0) {
  process.stderr.write('Usage: node e2e/design-shots.mjs <base address> <out dir> <path>...\n');
  process.exit(1);
}

mkdirSync(out, { recursive: true });
const engineName = process.env.PW_BROWSER ?? 'chromium';
const engine = { chromium, firefox, webkit }[engineName];
if (!engine) {
  process.stderr.write(`Unknown PW_BROWSER: ${engineName}
`);
  process.exit(1);
}
const channel = engineName === 'chromium' ? process.env.PW_CHANNEL : undefined;
const browser = await engine.launch(channel ? { channel } : {});
const sizes = [
  { name: 'phone', width: 390, height: 844, deviceScaleFactor: 2, isMobile: true, hasTouch: true },
  { name: 'desktop', width: 1440, height: 900, deviceScaleFactor: 1 },
];
for (const raw of paths) {
  const bang = /!(\d*)$/.exec(raw);
  const press = bang !== null;
  const wait = bang?.[1] ? Number(bang[1]) : 3600;
  const path = press ? raw.slice(0, bang.index) : raw;
  for (const size of sizes) {
    const page = await browser.newPage({
      viewport: { width: size.width, height: size.height },
      deviceScaleFactor: size.deviceScaleFactor,
      // Firefox has no mobile emulation: it takes the phone size only
      ...(engineName !== 'firefox' && size.isMobile ? { isMobile: true, hasTouch: true } : {}),
      locale: 'ru-RU',
      reducedMotion: process.env.SHOTS_MOTION === 'on' ? 'no-preference' : 'reduce',
    });
    await page.goto(new URL(path, base).toString(), { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(600);
    if (press) {
      await page.locator('.btn-main').first().click();
      await page.waitForTimeout(wait);
    }
    const slug = path.replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '') || 'home';
    const engineTag = engineName === 'chromium' ? '' : `-${engineName}`;
    const moment = press ? (bang[1] ? `-at${bang[1]}` : '-result') : '';
    const name = `${slug}${moment}-${size.name}${engineTag}.png`;
    await page.screenshot({ path: join(out, name), fullPage: false });
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    );
    process.stdout.write(`${name}${overflow ? ' — horizontal scroll!' : ''}\n`);
    await page.close();
  }
}

await browser.close();
