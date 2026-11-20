import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const sampleError = 'Docker did not return a valid memory sample.';

async function readDockerUsage(containerId) {
  const { stdout } = await exec(
    'docker',
    ['stats', '--no-stream', '--format', '{{.MemUsage}}', containerId],
    { encoding: 'utf8', timeout: 30_000, windowsHide: true },
  );
  return stdout;
}

/** Sample without blocking the workload or starting a second stats process while one is running. */
export function startMemorySampler(containerId, readUsage = readDockerUsage) {
  const samples = [];
  let pending;
  let error;

  function sample() {
    if (pending || error) return;
    pending = Promise.resolve()
      .then(() => readUsage(containerId))
      .then((out) => {
        const match = /^\s*(\d+(?:\.\d+)?)\s*(KiB|MiB|GiB)\b/.exec(out);
        if (!match) throw new Error(sampleError);
        samples.push(Number(match[1]) * { KiB: 1 / 1024, MiB: 1, GiB: 1024 }[match[2]]);
      })
      .catch(() => {
        error = sampleError;
      })
      .finally(() => {
        pending = undefined;
      });
    return pending;
  }

  const timer = setInterval(sample, 2000);
  async function stop() {
    clearInterval(timer);
    await pending;
  }

  return {
    get error() {
      return error;
    },
    get sampleCount() {
      return samples.length;
    },
    stop,
    async finish() {
      await stop();
      await sample();
      return samples;
    },
  };
}
