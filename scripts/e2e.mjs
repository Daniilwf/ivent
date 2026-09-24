// npm run test:e2e [-- <playwright args>]
// npm run test:smoke -- <address>
// Uses the bundled Chromium when installed; otherwise, locally on Windows, falls back to Edge.
import { existsSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { quote, root, run } from './lib.mjs';

const [mode, ...rest] = process.argv.slice(2);
const env = {};
const args = ['playwright', 'test'];

if (mode === 'smoke') {
  const address = rest.shift();
  if (!address) {
    process.stderr.write('Usage: npm run test:smoke -- <address>\n');
    process.exit(1);
  }
  env.E2E_BASE_URL = address;
  args.push('--grep', '@smoke');
}
args.push(...rest.map(quote));

if (
  !process.env.PW_CHANNEL &&
  !process.env.CI &&
  process.platform === 'win32' &&
  !bundledChromiumInstalled()
) {
  process.stdout.write(
    'Bundled Chromium is not installed, using Microsoft Edge (PW_CHANNEL=msedge).\n',
  );
  env.PW_CHANNEL = 'msedge';
}

process.exit(run('npx', args, { cwd: join(root, 'e2e'), env }) ? 0 : 1);

function bundledChromiumInstalled() {
  const dir =
    process.env.PLAYWRIGHT_BROWSERS_PATH ?? join(homedir(), 'AppData', 'Local', 'ms-playwright');
  return existsSync(dir) && readdirSync(dir).some((name) => name.startsWith('chromium-'));
}
