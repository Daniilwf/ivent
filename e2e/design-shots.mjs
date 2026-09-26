// Screenshots of the design previews (G2) or of any pages, at the two sizes of docs/DESIGN.md: 390×844 and 1440×900.
//   node e2e/design-shots.mjs <base address> <out dir> <path>...
// A path may carry an anchor (design-preview.html#wheel); «!» at its end presses the page's main button first and
// waits for the moment to end, so the shot shows the result.
// Uses the bundled Chromium when installed, Edge otherwise (PW_CHANNEL=msedge).
import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const [base, out, ...paths] = process.argv.slice(2);
if (!base || !out || paths.length === 0) {
  process.stderr.write('Usage: node e2e/design-shots.mjs <base address> <out dir> <path>...\n');
  process.exit(1);
}

mkdirSync(out, { recursive: true });
const channel = process.env.PW_CHANNEL;
const browser = await chromium.launch(channel ? { channel } : {});
const sizes = [
  { name: 'phone', width: 390, height: 844, deviceScaleFactor: 2 },
  { name: 'desktop', width: 1440, height: 900, deviceScaleFactor: 1 },
];
for (const raw of paths) {
  const press = raw.endsWith('!');
  const path = press ? raw.slice(0, -1) : raw;
  for (const size of sizes) {
    const page = await browser.newPage({
      viewport: { width: size.width, height: size.height },
      deviceScaleFactor: size.deviceScaleFactor,
      locale: 'ru-RU',
      reducedMotion: 'reduce',
    });
    await page.goto(new URL(path, base).toString(), { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(600);
    if (press) {
      await page.locator('.btn-main').first().click();
      await page.waitForTimeout(3600);
    }
    const slug = path.replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '') || 'home';
    const name = `${slug}${press ? '-result' : ''}-${size.name}.png`;
    await page.screenshot({ path: join(out, name), fullPage: false });
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    );
    process.stdout.write(`${name}${overflow ? ' — horizontal scroll!' : ''}\n`);
    await page.close();
  }
}

await browser.close();
