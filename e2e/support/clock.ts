import { mkdir, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { baseURL } from './world.ts';

// The site's clock is one for all tests (the test endpoint moves it for everyone, D-230). A scenario that moves it, or
// whose steps must stay inside a window of time, holds this lock for that part: the workers of one run (and of the phone
// and desktop projects) take turns there, the rest of the suite goes on in parallel.

const staleAfterMs = 3 * 60_000;

function lockDir() {
  const address = new URL(baseURL());
  return join(tmpdir(), `ivent-e2e-clock-${address.hostname}-${address.port}`);
}

export async function holdingTheClock<T>(work: () => Promise<T>): Promise<T> {
  const dir = lockDir();
  for (;;) {
    try {
      await mkdir(dir);
      break;
    } catch {
      // A lock left by a killed run is older than any test may take
      const since = await stat(dir).then(
        (s) => Date.now() - s.mtimeMs,
        () => 0,
      );
      if (since > staleAfterMs) await rm(dir, { recursive: true, force: true });
      else await delay(200);
    }
  }
  try {
    return await work();
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
}
