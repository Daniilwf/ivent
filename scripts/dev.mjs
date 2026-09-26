// npm run dev — backend (http://localhost:5080) and Vite with hot reload (http://localhost:5173, proxies /api and /hubs)
// on the small development seed in var/dev.db. Accounts: admin, vasya, petya, masha, kolya, dasha, zritel — the
// password is DevSeed:Password in src/GameEvent.Web/appsettings.Development.json.
// npm run dev:demo — the same on the demo season in var/demo.db (admin, zritel, player01…player16; F2, D-126).
// npm run seed:demo — build the demo season again from scratch and exit.
import { spawn } from 'node:child_process';
import { rmSync } from 'node:fs';
import { join } from 'node:path';
import { root, run } from './lib.mjs';

const demo = process.argv.includes('--demo');
const seedOnly = process.argv.includes('--seed-only');
const web = join(root, 'src', 'GameEvent.Web');
const env = {
  ...process.env,
  ASPNETCORE_ENVIRONMENT: 'Development',
  ...(demo ? { ConnectionStrings__Main: `Data Source=${join(root, 'var', 'demo.db')}` } : {}),
};

// The demo season is built once per database: seed:demo starts from an empty one
if (demo && seedOnly)
  for (const suffix of ['', '-wal', '-shm'])
    rmSync(join(root, 'var', `demo.db${suffix}`), { force: true });

if (!run('dotnet', ['run', '--project', web, '--', demo ? 'seed-demo' : 'seed-dev'], { env }))
  process.exit(1);
if (seedOnly) process.exit(0);

const children = [
  spawn('dotnet', ['watch', '--project', web, 'run', '--launch-profile', 'http'], {
    cwd: root,
    env,
    stdio: 'inherit',
    shell: process.platform === 'win32',
  }),
  spawn('npm', ['--prefix', 'web', 'run', 'dev'], {
    cwd: root,
    stdio: 'inherit',
    shell: process.platform === 'win32',
  }),
];

// With a shell on Windows, kill() stops only cmd.exe: take down the whole tree so no server keeps a port.
const stop = () =>
  children.forEach((c) => {
    if (process.platform === 'win32' && c.pid)
      spawn('taskkill', ['/pid', String(c.pid), '/T', '/F']);
    else c.kill();
  });
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
for (const child of children) child.on('exit', stop);
