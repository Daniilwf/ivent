// npm run dev
// Backend (http://localhost:5080) and Vite with hot reload (http://localhost:5173, proxies /api and /hubs)
// on the small development seed in var/dev.db. Accounts: admin, vasya, petya, masha, zritel — the password
// is DevSeed:Password in src/GameEvent.Web/appsettings.Development.json.
import { spawn } from 'node:child_process';
import { join } from 'node:path';
import { root, run } from './lib.mjs';

const web = join(root, 'src', 'GameEvent.Web');
const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' };

if (!run('dotnet', ['run', '--project', web, '--', 'seed-dev'], { env })) process.exit(1);

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
