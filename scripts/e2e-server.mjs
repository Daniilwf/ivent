// The site for E2E tests, started by Playwright (e2e/playwright.config.ts → webServer):
// a fresh database with the development seed, the built frontend served by the backend on :5080.
import { rmSync } from 'node:fs';
import { join } from 'node:path';
import { root, run } from './lib.mjs';

const web = join(root, 'src', 'GameEvent.Web');
const database = join(root, 'var', 'e2e.db');
const env = {
  ASPNETCORE_ENVIRONMENT: 'Development',
  ASPNETCORE_URLS: 'http://localhost:5080',
  ConnectionStrings__Main: `Data Source=${database}`,
};

for (const suffix of ['', '-wal', '-shm']) rmSync(database + suffix, { force: true });

const ok =
  run('npm', ['--prefix', 'web', 'run', '-s', 'build'], { cwd: root }) &&
  run('dotnet', ['build', web, '-nologo', '-v', 'q'], { cwd: root }) &&
  run('dotnet', ['run', '--no-build', '--project', web, '--', 'seed-dev'], { env });
if (!ok) process.exit(1);

// Runs until Playwright stops it.
process.exit(
  run('dotnet', ['run', '--no-build', '--project', web, '--no-launch-profile'], { env }) ? 0 : 1,
);
