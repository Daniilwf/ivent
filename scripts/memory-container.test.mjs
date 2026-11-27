import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, isAbsolute, join, relative, resolve, sep } from 'node:path';
import test from 'node:test';
import { withMemoryContainer } from './memory-container.mjs';

// A failed Docker start can already have created a container. The benchmark owns only the
// id written to its unique cidfile, never an unrelated container sharing its requested name.
const image = 'ivent:memory-container-test';
const name = 'ivent-memory-container-test';
const port = 18080;
const foreignId = 'f'.repeat(64);

function dataDirectory(t) {
  const root = mkdtempSync(join(tmpdir(), 'ivent-memory-container-test-'));
  t.after(() => {
    const resolved = resolve(root);
    const insideTemp = relative(resolve(tmpdir()), resolved);
    assert.ok(
      insideTemp &&
        insideTemp !== '..' &&
        !insideTemp.startsWith(`..${sep}`) &&
        !isAbsolute(insideTemp),
    );
    assert.ok(basename(resolved).startsWith('ivent-memory-container-test-'));
    rmSync(resolved, { recursive: true, force: true });
  });
  return root;
}

function fakeDocker(data, { start = 'success', cleanup = 'success', cid } = {}) {
  const calls = [];
  const cidfiles = [];
  const created = [];
  const removed = [];
  const containers = new Set([foreignId]);
  const startError = new Error('The Docker CLI timed out after creating the container.');
  let sequence = 0;
  const docker = (...args) => {
    calls.push(args);
    if (args[0] === 'run') {
      const position = args.indexOf('--cidfile');
      assert.ok(position >= 0, 'Docker writes ownership into a cidfile even on a partial failure.');
      const cidfile = args[position + 1];
      assert.equal(typeof cidfile, 'string');
      const insideData = relative(resolve(data), resolve(cidfile));
      assert.ok(
        insideData &&
          insideData !== '..' &&
          !insideData.startsWith(`..${sep}`) &&
          !isAbsolute(insideData),
      );
      assert.equal(existsSync(cidfile), false, 'A previous cidfile cannot be reused.');
      assert.ok(!cidfiles.includes(cidfile), 'Every attempt has a unique ownership file.');
      cidfiles.push(cidfile);
      if (start === 'refuse-without-create') {
        return { ok: false, out: `The name ${name} belongs to another container.` };
      }
      sequence++;
      const id = sequence.toString(16).padStart(64, 'a');
      containers.add(id);
      created.push(id);
      writeFileSync(cidfile, cid ?? `${id}\n`);
      if (start === 'throw-after-create') throw startError;
      if (start === 'fail-after-create') return { ok: false, out: 'Port binding failed.' };
      return { ok: true, out: id };
    }
    if (args[0] === 'rm') {
      const ids = args.slice(1).filter((arg) => !arg.startsWith('-'));
      assert.equal(ids.length, 1, 'Cleanup addresses exactly the container this attempt created.');
      assert.match(
        ids[0],
        /^[0-9a-f]{64}$/i,
        'Docker removal is never addressed by name or malformed id.',
      );
      assert.notEqual(ids[0], foreignId, 'The existing foreign container is never removed.');
      assert.ok(created.includes(ids[0]), 'Only an id created by this attempt can be removed.');
      removed.push(ids[0]);
      if (cleanup === 'throw') throw new Error('Docker cleanup transport failed.');
      if (cleanup === 'failure')
        return { ok: false, out: 'Docker could not remove the container.' };
      containers.delete(ids[0]);
      return { ok: true, out: ids[0] };
    }
    if (args[0] === 'logs') return { ok: true, out: '' };
    throw new Error(`Unexpected Docker operation: ${args[0]}`);
  };
  return { docker, calls, cidfiles, created, removed, containers, startError };
}

function options(data, fake) {
  return { data, docker: fake.docker, image, name, port };
}

function argument(args, flag) {
  const at = args.indexOf(flag);
  assert.ok(at >= 0, `The start command has ${flag}.`);
  return args[at + 1];
}

test('the benchmark binds only loopback and keeps its production image and resource limits', async (t) => {
  const data = dataDirectory(t);
  const fake = fakeDocker(data);
  let callsToWork = 0;

  await withMemoryContainer(options(data, fake), async (containerId) => {
    callsToWork++;
    assert.equal(containerId, fake.created[0]);
    assert.ok(fake.containers.has(containerId), 'The workload runs before cleanup.');
    assert.equal(readFileSync(fake.cidfiles[0], 'utf8').trim(), containerId);
  });

  const run = fake.calls.find((args) => args[0] === 'run');
  assert.ok(run);
  const published = run.includes('--publish') ? argument(run, '--publish') : argument(run, '-p');
  assert.equal(published, `127.0.0.1:${port}:8080`);
  assert.equal(argument(run, '--memory'), '512m');
  assert.equal(Number(argument(run, '--cpus')), 1);
  assert.ok(run.includes('--read-only'));
  assert.equal(argument(run, '--tmpfs'), '/tmp:size=256m');
  assert.ok(run.includes(image));
  assert.equal(run.at(-1), image, 'The production image has no command-line override.');
  assert.equal(run.includes('ASPNETCORE_ENVIRONMENT=Development'), false);
  assert.equal(run.includes('ASPNETCORE_ENVIRONMENT=Staging'), false);
  assert.equal(
    run.some((arg) => /^(ASPNETCORE|DOTNET)_ENVIRONMENT=(?!Production$)/i.test(arg)),
    false,
  );
  assert.equal(callsToWork, 1);
  assert.deepEqual(fake.removed, fake.created);
  assert.deepEqual([...fake.containers], [foreignId]);
});

test('sequential attempts use distinct cidfiles inside the given data directory', async (t) => {
  const data = dataDirectory(t);
  const fake = fakeDocker(data);

  await withMemoryContainer(options(data, fake), async () => {});
  await withMemoryContainer(options(data, fake), async () => {});

  assert.equal(fake.cidfiles.length, 2);
  assert.notEqual(fake.cidfiles[0], fake.cidfiles[1]);
  assert.deepEqual(fake.removed, fake.created);
});

for (const start of ['fail-after-create', 'throw-after-create']) {
  test(`${start} still removes the id written during Docker creation`, async (t) => {
    const data = dataDirectory(t);
    const fake = fakeDocker(data, { start });
    let workStarted = false;

    await assert.rejects(() =>
      withMemoryContainer(options(data, fake), async () => {
        workStarted = true;
      }),
    );

    assert.equal(workStarted, false);
    assert.equal(fake.created.length, 1);
    assert.deepEqual(fake.removed, fake.created);
    assert.deepEqual([...fake.containers], [foreignId]);
  });
}

test('a start refusal without a cidfile does not remove the existing container with that name', async (t) => {
  const data = dataDirectory(t);
  const fake = fakeDocker(data, { start: 'refuse-without-create' });
  let workStarted = false;

  await assert.rejects(() =>
    withMemoryContainer(options(data, fake), async () => {
      workStarted = true;
    }),
  );

  assert.equal(workStarted, false);
  assert.deepEqual(fake.created, []);
  assert.deepEqual(fake.removed, []);
  assert.deepEqual([...fake.containers], [foreignId]);
});

test('a workload failure still removes its owned container and remains a failure', async (t) => {
  const data = dataDirectory(t);
  const fake = fakeDocker(data);
  const error = new Error('The measured API returned an error.');

  await assert.rejects(
    () =>
      withMemoryContainer(options(data, fake), async () => {
        throw error;
      }),
    (failure) => failure === error,
  );

  assert.deepEqual(fake.removed, fake.created);
  assert.deepEqual([...fake.containers], [foreignId]);
});

for (const cleanup of ['failure', 'throw']) {
  test(`cleanup ${cleanup} prevents a successful workload from claiming success`, async (t) => {
    const data = dataDirectory(t);
    const fake = fakeDocker(data, { cleanup });
    let measured = false;

    await assert.rejects(() =>
      withMemoryContainer(options(data, fake), async () => {
        measured = true;
      }),
    );

    assert.equal(measured, true);
    assert.deepEqual(fake.removed, fake.created);
  });
}

for (const cid of ['', name, '../another-container', `${'a'.repeat(64)}\n${foreignId}`]) {
  test(`a malformed cidfile (${JSON.stringify(cid)}) is never passed to docker rm`, async (t) => {
    const data = dataDirectory(t);
    const fake = fakeDocker(data, { cid });
    let workStarted = false;

    await assert.rejects(() =>
      withMemoryContainer(options(data, fake), async () => {
        workStarted = true;
      }),
    );

    assert.equal(
      workStarted,
      false,
      'The stdout id cannot substitute for missing ownership proof.',
    );
    assert.deepEqual(fake.removed, []);
    assert.ok(fake.containers.has(foreignId));
  });
}
