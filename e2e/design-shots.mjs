// Screenshots of the design previews (G2) or of any pages, at the two sizes of docs/DESIGN.md: 390×844 and 1440×900.
//   node e2e/design-shots.mjs <base address> <out dir> <path>...
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
for (const path of paths) {
  for (const size of sizes) {
    const page = await browser.newPage({
      viewport: { width: size.width, height: size.height },
      deviceScaleFactor: size.deviceScaleFactor,
      locale: 'ru-RU',
      reducedMotion: 'reduce',
    });
    await page.goto(new URL(path, base).toString(), { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    const name = `${path.replaceAll('/', '-').replace(/^-|-$/g, '') || 'home'}-${size.name}.png`;
    await page.screenshot({ path: join(out, name), fullPage: false });
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth,
    );
    process.stdout.write(`${name}${overflow ? ' — horizontal scroll!' : ''}\n`);
    await page.close();
  }
}

await browser.close();
