import { randomUUID } from 'node:crypto';
import { existsSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

// The CLI writes the CID on creation, including a container whose subsequent port binding fails.
// A unique file distinguishes our partial start from another process taking the requested name.
export async function withMemoryContainer({ docker, image, data, name, port }, work) {
  const cidfile = join(data, `container-${randomUUID()}.cid`);
  function ownedId() {
    if (!existsSync(cidfile)) return null;
    const id = readFileSync(cidfile, 'utf8').trim();
    if (!/^[a-f0-9]{64}$/i.test(id)) throw new Error(`Invalid benchmark container ID: ${cidfile}`);
    return id;
  }

  let id;
  let result;
  let failure;
  try {
    const started = docker(
      'run',
      '-d',
      '--name',
      name,
      '--cidfile',
      cidfile,
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
      `127.0.0.1:${port}:8080`,
      '-v',
      `${data}:/data`,
      '-e',
      'AllowedHosts=localhost',
      // The login rate is not being measured; all page-read limits stay at their production values.
      '-e',
      'Security__LoginAttemptsPerMinute=1000',
      '-e',
      'Proxy__KnownNetworks__0=0.0.0.0/0',
      image,
    );
    if (!started.ok) throw new Error(started.out);
    id = ownedId();
    if (!id) throw new Error('Docker did not write the benchmark container ID.');
    result = await work(id);
  } catch (error) {
    failure = error;
  } finally {
    try {
      id ??= ownedId();
      if (id) {
        const removed = docker('rm', '-f', id);
        if (!removed.ok)
          throw new Error(`Could not remove benchmark container ${id}: ${removed.out}`);
        rmSync(cidfile, { force: true });
      }
    } catch (error) {
      failure = failure
        ? new AggregateError([failure, error], 'Benchmark and cleanup failed.')
        : error;
    }
  }
  if (failure) throw failure;
  return result;
}
