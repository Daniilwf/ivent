// The memory check (CLAUDE.md «Эксплуатация», SPEC «Хостинг»): the live site's process must stay within its budget
// (400 MB by default) on a 1-core, 1 GB server, under the demo season's load.
//   1. Seeds the demo season locally (Development) into var/memory-check/game-event.db.
//   2. Builds the site's image and starts it as the live site does: Production, the container's memory and CPU limits
//      of deploy/docker-compose.yml, the demo database mounted as /data.
//   3. Sixteen players read season, feed, pool, rules, status, polls, global events and weekly challenges, while one
//      admin reads the manual effects and IRL proof queues and the container's memory is sampled every two seconds.
//   4. Prints the peak and the average; fails when the peak is over the budget.
// Usage: npm run test:memory [-- --seconds 120 --budget 400 --image ivent:local]   (needs Docker)
import { existsSync, lstatSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fail, quote, root, run } from './lib.mjs';
import {
  assertIrlMemoryReads,
  assertStage5MemoryReads,
  prepareStage5MemoryWorkload,
} from './memory-stage5.mjs';
import { withMemoryContainer } from './memory-container.mjs';
import { startMemorySampler } from './memory-sampler.mjs';

const arg = (name, fallback) => {
  const at = process.argv.indexOf(`--${name}`);
  return at >= 0 ? process.argv[at + 1] : fallback;
};
const seconds = Number(arg('seconds', '120'));
const budget = Number(arg('budget', '400'));
if (!Number.isFinite(seconds) || seconds <= 0 || !Number.isFinite(budget) || budget <= 0)
  fail('Seconds and memory budget must be positive finite numbers.');
const prebuilt = arg('image', null);
const image = prebuilt ?? 'ivent:memory-check';
const port = 18080;
const container = 'ivent-memory-check';
const data = resolve(root, 'var', 'memory-check');
const players = Array.from({ length: 16 }, (_, i) => `player${String(i + 1).padStart(2, '0')}`);

// docker runs without a shell: arguments reach it as they are
function docker(...args) {
  const result = spawnSync('docker', args, { encoding: 'utf8', timeout: 30_000 });
  return { ok: result.status === 0, out: `${result.stdout ?? ''}${result.stderr ?? ''}`.trim() };
}

const abort = new AbortController();

async function main() {
  if (!docker('info', '--format', '{{.ServerVersion}}').ok)
    throw new Error('Start Docker before running the memory check.');
  if (docker('container', 'inspect', container).ok)
    throw new Error(`Container ${container} already exists; another benchmark may be running.`);
  // Check the exact absolute deletion target and reject redirected folders before touching the scratch database.
  if (relative(root, data) !== ['var', 'memory-check'].join(sep))
    throw new Error('The scratch database must stay inside this workspace.');
  for (const path of [join(root, 'var'), data])
    if (existsSync(path) && lstatSync(path).isSymbolicLink())
      throw new Error(`Refusing a redirected scratch folder: ${path}`);

  // 1. The demo season, seeded into a disposable folder. No user's database is mounted.
  rmSync(data, { recursive: true, force: true });
  mkdirSync(data, { recursive: true });
  const web = join(root, 'src', 'GameEvent.Web');
  if (!run('dotnet', ['build', quote(web), '-nologo', '-v', 'q']))
    throw new Error('The site does not build.');
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
    throw new Error('The demo season was not seeded.');

  // 2. The image and the container, with the live site's limits (deploy/docker-compose.yml, service `site`)
  if (!prebuilt && !run('docker', ['build', '-q', '-f', 'deploy/Dockerfile', '-t', image, '.']))
    throw new Error('The image does not build.');
  try {
    await withMemoryContainer(
      { docker, image, data, name: container, port },
      async (containerId) => {
        let sampler;
        let requests = 0;
        let adminRequests = 0;
        let errors = 0;
        try {
          const base = `http://localhost:${port}`;
          const deadline = Date.now() + 120_000;
          let ready = false;
          while (Date.now() < deadline) {
            try {
              if ((await fetch(`${base}/health`, { signal: AbortSignal.timeout(5000) })).ok) {
                ready = true;
                break;
              }
            } catch {
              // not up yet
            }
            await new Promise((r) => setTimeout(r, 1000));
          }
          if (!ready)
            throw new Error('The benchmark site did not become healthy within 120 seconds.');
          sampler = startMemorySampler(containerId);

          // 3. The players, an authenticated admin and real stage 5 data.

          async function signIn(login) {
            const jar = new Map();
            const call = async (path, init = {}) => {
              const headers = new Headers(init.headers);
              if (
                !['GET', 'HEAD', 'OPTIONS'].includes(init.method ?? 'GET') &&
                !headers.has('X-CSRF-TOKEN')
              ) {
                const tokenResponse = await call('/api/auth/antiforgery');
                if (!tokenResponse.ok)
                  throw new Error('Could not obtain the authenticated CSRF token.');
                headers.set('X-CSRF-TOKEN', (await tokenResponse.json()).token);
              }
              headers.set('X-Forwarded-Proto', 'https');
              headers.set('cookie', [...jar].map(([k, v]) => `${k}=${v}`).join('; '));
              const response = await fetch(`${base}${path}`, {
                ...init,
                redirect: 'manual',
                signal: AbortSignal.any([abort.signal, AbortSignal.timeout(15_000)]),
                headers,
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

          const clients = await Promise.all(players.map(signIn));
          const current = await clients[0]('/api/seasons/current');
          if (!current.ok) throw new Error(`No benchmark season: HTTP ${current.status}`);
          const season = await current.json();
          const id = season.id ?? season.seasonId;
          if (!id) throw new Error('The demo season has no id.');
          const admin = await signIn('admin');
          const irlProof = await prepareStage5MemoryWorkload({
            admin,
            players: clients,
            seasonId: id,
            content: {
              objects: [
                JSON.parse(readFileSync(join(root, 'content', 'events', 'tailwind.json'), 'utf8')),
                JSON.parse(readFileSync(join(root, 'content', 'events', 'karaoke.json'), 'utf8')),
              ],
              wheels: [],
            },
          });
          console.log(
            'Stage 5 workload prepared: votes, an active goal, a completed weekly challenge, a waiting event, and a pending IRL proof.',
          );
          const pages = [
            `/api/seasons/${id}`,
            `/api/seasons/${id}/feed`,
            `/api/seasons/${id}/games`,
            `/api/seasons/${id}/rules`,
            '/api/pool',
            '/api/status',
            '/api/seasons/current',
            // Stage 5 (5.19, D-837): they read the projection's tables
            `/api/seasons/${id}/polls`,
            `/api/seasons/${id}/global-events`,
            `/api/seasons/${id}/weekly-challenges`,
          ];
          const end = Date.now() + seconds * 1000;
          const visit = async (call, routes, delay, cursor, isAdmin = false) => {
            let i = cursor;
            while (Date.now() < end) {
              if (sampler.error) throw new Error(sampler.error);
              const response = await call(routes[i++ % routes.length]).catch(() => null);
              requests++;
              if (isAdmin) adminRequests++;
              if (!response?.ok) errors++;
              await response?.arrayBuffer().catch(() => {
                errors++;
              });
              await new Promise((r) => setTimeout(r, delay));
            }
          };
          await Promise.all([
            // At most two reads/sec/player: three stage 5 routes share 60 reads/min, including setup.
            ...clients.map((call, n) => visit(call, pages, 500, n)),
            // Admin reads share the 30/min limit, including the reads before and after load.
            visit(
              admin,
              [`/api/admin/seasons/${id}/irl-proofs`, `/api/admin/seasons/${id}/effects`],
              4000,
              0,
              true,
            ),
          ]);
          await assertStage5MemoryReads({ call: clients[0], seasonId: id });
          await assertIrlMemoryReads({
            admin,
            player: clients[irlProof.playerIndex],
            seasonId: id,
            ...irlProof,
          });
          const samples = await sampler.finish();
          const samplingError = sampler.error;
          const peak = Math.max(...samples);
          const average = samples.reduce((a, b) => a + b, 0) / samples.length;
          console.log(
            `${players.length} players and one admin, ${seconds} s, ${requests} requests (${adminRequests} admin, ${errors} failed); memory peak ${peak.toFixed(1)} MiB, average ${average.toFixed(1)} MiB, budget ${budget} MiB`,
          );
          if (
            samplingError ||
            !samples.length ||
            errors > 0 ||
            adminRequests === 0 ||
            requests === adminRequests
          )
            throw new Error(
              samplingError ?? 'The load had failed requests or no samples/requests.',
            );
          if (peak > budget)
            throw new Error(`Over the budget: ${peak.toFixed(1)} MiB > ${budget} MiB.`);
          console.log('Within the budget.');
        } catch (error) {
          // What the site said about it: the container's log goes with it
          console.error(docker('logs', '--tail', '80', containerId).out);
          throw error;
        } finally {
          abort.abort();
          await sampler?.stop();
        }
      },
    );
  } finally {
    if (!prebuilt) docker('image', 'rm', '-f', image);
  }
}

await main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
