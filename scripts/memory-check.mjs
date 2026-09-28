// The memory check (CLAUDE.md «Эксплуатация», SPEC «Хостинг»): the live site's process must stay within its budget
// (400 MB by default) on a 1-core, 1 GB server, under the demo season's load.
//   1. Seeds the demo season locally (Development) into var/memory-check/game-event.db.
//   2. Builds the site's image and starts it as the live site does: Production, the container's memory and CPU limits
//      of deploy/docker-compose.yml, the demo database mounted as /data.
//   3. Sixteen players sign in and walk the pages (season, feed, pool, rules, status) for the given time, while the
//      container's memory is sampled every two seconds.
//   4. Prints the peak and the average; fails when the peak is over the budget.
// Usage: npm run test:memory [-- --seconds 120 --budget 400 --image ivent:local]   (needs Docker)
import { mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fail, quote, root, run } from './lib.mjs';

const arg = (name, fallback) => {
  const at = process.argv.indexOf(`--${name}`);
  return at >= 0 ? process.argv[at + 1] : fallback;
};
const seconds = Number(arg('seconds', '120'));
const budget = Number(arg('budget', '400'));
const prebuilt = arg('image', null);
const image = prebuilt ?? 'ivent:memory-check';
const port = 18080;
const container = 'ivent-memory-check';
const data = join(root, 'var', 'memory-check');
const players = Array.from({ length: 16 }, (_, i) => `player${String(i + 1).padStart(2, '0')}`);

// docker runs without a shell: arguments reach it as they are
function docker(...args) {
  const result = spawnSync('docker', args, { encoding: 'utf8' });
  return { ok: result.status === 0, out: `${result.stdout ?? ''}${result.stderr ?? ''}`.trim() };
}

function stop() {
  docker('rm', '-f', container);
}

// 1. The demo season, seeded as `npm run seed:demo` does, into a folder the container gets as /data
rmSync(data, { recursive: true, force: true });
mkdirSync(data, { recursive: true });
const web = join(root, 'src', 'GameEvent.Web');
if (!run('dotnet', ['build', quote(web), '-nologo', '-v', 'q'])) fail('The site does not build.');
if (
  !run(
    'dotnet',
    ['run', '--no-build', '--no-launch-profile', '--project', quote(web), '--', 'seed-demo'],
    {
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ConnectionStrings__Main: `Data Source=${join(data, 'game-event.db')}`,
      },
    },
  )
)
  fail('The demo season was not seeded.');

// 2. The image and the container, with the live site's limits (deploy/docker-compose.yml, service `site`)
if (!prebuilt && !run('docker', ['build', '-q', '-f', 'deploy/Dockerfile', '-t', image, '.']))
  fail('The image does not build.');
stop();
const started = docker(
  'run',
  '-d',
  '--name',
  container,
  '--memory',
  '512m',
  '--memory-swap',
  '512m',
  '--cpus',
  '1',
  '--read-only',
  '--tmpfs',
  '/tmp:size=256m',
  '-p',
  `${port}:8080`,
  '-v',
  `${data}:/data`,
  '-e',
  'AllowedHosts=localhost',
  // Sixteen sign-ins from one address in a minute: the login limit is not what is measured
  '-e',
  'Security__LoginAttemptsPerMinute=1000',
  // The live site sits behind Caddy, which says the request came over HTTPS; here the script says it (D-107)
  '-e',
  'Proxy__KnownNetworks__0=0.0.0.0/0',
  image,
);
if (!started.ok) fail(started.out);

const base = `http://localhost:${port}`;
const deadline = Date.now() + 120_000;
while (Date.now() < deadline) {
  try {
    if ((await fetch(`${base}/health`)).ok) break;
  } catch {
    // not up yet
  }
  await new Promise((r) => setTimeout(r, 1000));
}

// 3. The players and the samples
const samples = [];
function sample() {
  const stats = docker('stats', '--no-stream', '--format', '{{.MemUsage}}', container);
  const m = /([\d.]+)\s*(KiB|MiB|GiB)/.exec(stats.out);
  if (m) samples.push(Number(m[1]) * { KiB: 1 / 1024, MiB: 1, GiB: 1024 }[m[2]]);
}

async function signIn(login) {
  const jar = new Map();
  const call = async (path, init = {}) => {
    const response = await fetch(`${base}${path}`, {
      ...init,
      redirect: 'manual',
      headers: {
        ...init.headers,
        'X-Forwarded-Proto': 'https',
        cookie: [...jar].map(([k, v]) => `${k}=${v}`).join('; '),
      },
    });
    for (const line of response.headers.getSetCookie()) {
      const [pair] = line.split(';');
      const at = pair.indexOf('=');
      jar.set(pair.slice(0, at), pair.slice(at + 1));
    }
    return response;
  };
  const token = async () => (await (await call('/api/auth/antiforgery')).json()).token;
  const login_ = await call('/api/auth/login', {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'X-CSRF-TOKEN': await token() },
    body: JSON.stringify({ login, password: 'dev-password' }),
  });
  if (!login_.ok) throw new Error(`${login} could not sign in: ${login_.status}`);
  return call;
}

let requests = 0;
let errors = 0;
try {
  const clients = await Promise.all(players.map(signIn));
  const season = await (await clients[0]('/api/seasons/current')).json();
  const id = season.id ?? season.seasonId;
  const pages = [
    `/api/seasons/${id}`,
    `/api/seasons/${id}/feed`,
    `/api/seasons/${id}/games`,
    `/api/seasons/${id}/rules`,
    '/api/pool',
    '/api/status',
    '/api/seasons/current',
  ];
  const end = Date.now() + seconds * 1000;
  const sampler = setInterval(sample, 2000);
  await Promise.all(
    clients.map(async (call, n) => {
      let i = n;
      while (Date.now() < end) {
        const response = await call(pages[i++ % pages.length]).catch(() => null);
        requests++;
        if (!response?.ok) errors++;
        await response?.arrayBuffer().catch(() => undefined);
      }
    }),
  );
  clearInterval(sampler);
  sample();
} catch (error) {
  // What the site said about it: the container's log goes with it
  console.error(docker('logs', '--tail', '80', container).out);
  throw error;
} finally {
  stop();
  if (!prebuilt) docker('image', 'rm', '-f', image);
}

// 4. The result
const peak = Math.max(...samples);
const average = samples.reduce((a, b) => a + b, 0) / samples.length;
console.log(
  `${players.length} players, ${seconds} s, ${requests} requests (${errors} failed); memory peak ${peak.toFixed(0)} MiB, average ${average.toFixed(0)} MiB, budget ${budget} MiB`,
);
if (!samples.length || errors > requests * 0.01)
  fail('The load did not run as it should: no samples or too many failed requests.');
if (peak > budget) fail(`Over the budget: ${peak.toFixed(0)} MiB > ${budget} MiB.`);
console.log('Within the budget.');
