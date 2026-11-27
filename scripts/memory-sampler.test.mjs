import assert from 'node:assert/strict';
import test from 'node:test';
import { startMemorySampler } from './memory-sampler.mjs';

test('slow Docker samples never overlap and stopping drains the active read', async (t) => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const pending = Promise.withResolvers();
  let reads = 0;
  const sampler = startMemorySampler('container', (id) => {
    assert.equal(id, 'container');
    reads++;
    return pending.promise;
  });

  t.mock.timers.tick(6000);
  await Promise.resolve();
  assert.equal(reads, 1);

  let stopped = false;
  const stopping = sampler.stop().then(() => {
    stopped = true;
  });
  t.mock.timers.tick(6000);
  await Promise.resolve();
  assert.equal(stopped, false);
  assert.equal(reads, 1);

  pending.resolve('1024 KiB / 512 MiB');
  await stopping;
  t.mock.timers.tick(6000);
  assert.equal(stopped, true);
  assert.equal(reads, 1);
  assert.equal(sampler.error, undefined);
});

test('the closing sample follows the active read and returns both values in MiB', async (t) => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const pending = Promise.withResolvers();
  let reads = 0;
  const sampler = startMemorySampler('container', () => {
    reads++;
    return reads === 1 ? pending.promise : Promise.resolve('0.25 GiB / 512 MiB');
  });

  t.mock.timers.tick(2000);
  const finishing = sampler.finish();
  await Promise.resolve();
  assert.equal(reads, 1);

  pending.resolve('183.7 MiB / 512 MiB');
  assert.deepEqual(await finishing, [183.7, 256]);
  t.mock.timers.tick(6000);
  assert.equal(reads, 2);
});

test('a bad interval read keeps the sampling error even if a later read could succeed', async (t) => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  let reads = 0;
  const sampler = startMemorySampler('container', () => {
    reads++;
    return Promise.resolve(reads === 1 ? 'invalid stats' : '128 MiB / 512 MiB');
  });

  t.mock.timers.tick(2000);
  assert.deepEqual(await sampler.finish(), []);
  assert.equal(sampler.error, 'Docker did not return a valid memory sample.');
  assert.equal(reads, 1);
});

test('a rejected Docker read is recorded as a sampling error', async (t) => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const sampler = startMemorySampler('container', () =>
    Promise.reject(new Error('Docker timeout')),
  );

  assert.deepEqual(await sampler.finish(), []);
  assert.equal(sampler.error, 'Docker did not return a valid memory sample.');
});
