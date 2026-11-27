import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

test('smoke disables local fixture setup even when the parent process opted in', () => {
  const preload = `
    import childProcess from 'node:child_process';
    import { writeSync } from 'node:fs';
    import { syncBuiltinESMExports } from 'node:module';

    childProcess.spawnSync = (command, args, { env }) => {
      writeSync(1, JSON.stringify({
        command,
        args,
        address: env.E2E_BASE_URL,
        localSite: env.E2E_LOCAL_SITE,
      }));
      return { status: 0 };
    };
    syncBuiltinESMExports();
  `;
  const address = 'http://127.0.0.1:1';
  const result = spawnSync(
    process.execPath,
    [
      '--import',
      `data:text/javascript,${encodeURIComponent(preload)}`,
      fileURLToPath(new URL('./e2e.mjs', import.meta.url)),
      'smoke',
      address,
    ],
    { encoding: 'utf8', env: { ...process.env, E2E_LOCAL_SITE: '1', PW_CHANNEL: 'chromium' } },
  );

  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    command: 'npx',
    args: ['playwright', 'test', '--grep', '@smoke'],
    address,
    localSite: '0',
  });
});
