// npm run test:visual [-- <playwright args>], e.g. -- --update-snapshots
// Builds the frontend here, then runs e2e/visual inside the Playwright container, so the screenshots match the
// baselines on any machine and in CI. Needs Docker. In CI (already inside that container) it runs directly.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { quote, root, run } from './lib.mjs';

const rest = process.argv.slice(2);
const inContainer = process.env.PLAYWRIGHT_CONTAINER === '1';

if (
  !process.env.VISUAL_SKIP_BUILD &&
  !run('npm', ['--prefix', 'web', 'run', '-s', 'build'], { cwd: root })
)
  process.exit(1);

const test = [
  'node',
  'node_modules/@playwright/test/cli.js',
  'test',
  '-c',
  'playwright.visual.config.ts',
  ...rest,
];

if (inContainer)
  process.exit(run(test[0], test.slice(1).map(quote), { cwd: join(root, 'e2e') }) ? 0 : 1);

// The container's Playwright must be the version the tests are written for
const version = JSON.parse(
  readFileSync(join(root, 'e2e/node_modules/@playwright/test/package.json'), 'utf8'),
).version;
const image = `mcr.microsoft.com/playwright:v${version}-noble`;
const ok = run('docker', [
  'run',
  '--rm',
  '--ipc=host',
  '-e',
  'CI',
  '-e',
  'PLAYWRIGHT_CONTAINER=1',
  '-v',
  `${root}:/work`,
  '-w',
  '/work/e2e',
  image,
  ...test.map(quote),
]);
process.exit(ok ? 0 : 1);
