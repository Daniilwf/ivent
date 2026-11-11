import { useEffect, useSyncExternalStore } from 'react';
import { api } from '../api/client';
import type { SeasonUpdate } from '../api/realtime';
import { useSeasonVersion } from '../app/useSeasonVersion';

/**
 * The admin's count of runs waiting for a proof check (D-359, D-263), for the way to «Админка» in the header. It is the
 * length of the proof queue (`GET /api/admin/seasons/{id}/proofs`, the same read the admin page makes): one number per
 * season, kept here so the header and the admin page share it. Away from the admin page the header reads the queue
 * itself, again only when the season's hub reports a change of the queue, at most once per `refreshEveryMs` (the
 * admin's reads are rate-limited); on the admin page the page's own reads report it, and the header asks nothing.
 */

const counts = new Map<string, number>();
const listeners = new Set<() => void>();

const refreshEveryMs = 10_000;

// The updates that change the queue: a completion joins it, a check or an undo leaves it; a catch-up without the list
// of updates always counts
const queueTypes = new Set([
  'run-completed',
  'proof-approved',
  'proof-rejected',
  'proof-reject-penalized',
  'command-undone',
]);
const changesQueue = (update: SeasonUpdate) => update.types.some((type) => queueTypes.has(type));

/** The admin page's queue read: the header shows it at once */
export function reportWaitingProofs(seasonId: string, count: number) {
  if (counts.get(seasonId) === count) return;
  counts.set(seasonId, count);
  for (const listener of listeners) listener();
}

/** Tests only: a fresh page */
export function forgetWaitingProofs() {
  counts.clear();
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/**
 * The count for the header: null while unknown or for anyone but the admin. `readOwn` — whether the header reads the
 * queue itself (away from the admin page)
 */
export function useWaitingProofs(seasonId: string | null, readOwn: boolean): number | null {
  const watched = readOwn ? seasonId : null;
  const version = useSeasonVersion(watched, { relevant: changesQueue, throttleMs: refreshEveryMs });
  useEffect(() => {
    if (!watched) return;
    let stopped = false;
    api
      .GET('/api/admin/seasons/{seasonId}/proofs', { params: { path: { seasonId: watched } } })
      .then((answer) => {
        if (!stopped && answer.data) reportWaitingProofs(watched, answer.data.length);
      })
      // A lost read keeps the last count: the next change of the queue asks again
      .catch(() => undefined);
    return () => {
      stopped = true;
    };
  }, [watched, version]);
  return useSyncExternalStore(subscribe, () => (seasonId ? (counts.get(seasonId) ?? null) : null));
}
