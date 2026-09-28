// The site for E2E tests, started by Playwright (e2e/playwright.config.ts → webServer): a fresh database with the demo
// season (scenario 13 measures the main page on it) and the development seed over it, the built frontend served by the
// backend on :5080 (E2E_PORT for another port, e.g. a second worktree).
import { rmSync } from 'node:fs';
import { join } from 'node:path';
import { root, run } from './lib.mjs';

const port = process.env.E2E_PORT ?? '5080';
const web = join(root, 'src', 'GameEvent.Web');
const database = join(root, 'var', port === '5080' ? 'e2e.db' : `e2e-${port}.db`);
const env = {
  ASPNETCORE_ENVIRONMENT: 'Development',
  ASPNETCORE_URLS: `http://localhost:${port}`,
  ConnectionStrings__Main: `Data Source=${database}`,
  // Every E2E test signs in its own accounts from one address: the per-minute sign-in limit is for people, not for the
  // suite. The failed-attempt throttle (D-67) stays as it is.
  Security__LoginAttemptsPerMinute: '10000',
  // The deadline scenario waits for the scheduler to close the season: a second, not five
  Scheduler__IntervalSeconds: '1',
};

for (const suffix of ['', '-wal', '-shm']) rmSync(database + suffix, { force: true });

// The demo season goes first: it refuses a database with other seasons in it (DemoSeed)
const ok =
  run('npm', ['--prefix', 'web', 'run', '-s', 'build'], { cwd: root }) &&
  run('dotnet', ['build', web, '-nologo', '-v', 'q'], { cwd: root }) &&
  run('dotnet', ['run', '--no-build', '--project', web, '--', 'seed-demo'], { env }) &&
  run('dotnet', ['run', '--no-build', '--project', web, '--', 'seed-dev'], { env });
if (!ok) process.exit(1);

// Runs until Playwright stops it.
process.exit(
  run('dotnet', ['run', '--no-build', '--project', web, '--no-launch-profile'], { env }) ? 0 : 1,
);
