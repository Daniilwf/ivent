import { useEffect, useEffectEvent, useRef, useState } from 'react';
import { watchPool, watchSeason, type SeasonUpdate } from '../api/realtime';

/**
 * A number that grows when the season's log moves on (D-202): a page passes it as `version` to useLoaded and reads its
 * data again. The first answer of the hub is the join itself — the page has just loaded — so it is skipped; a
 * reconnection or a catch-up counts (names and avatars live outside the log, D-122).
 *
 * - `relevant` — only these updates count (the rules page: a change of the rules or the deadline, an undo); a
 *   catch-up without the list of updates always counts.
 * - `debounceMs` — one read after a burst (a roll, its misses, a move come together).
 * - `throttleMs` — at most one read per period (the admin's reads are rate-limited).
 */
export function useSeasonVersion(
  seasonId: string | null,
  {
    relevant,
    debounceMs = 0,
    throttleMs = 0,
  }: {
    relevant?: (update: SeasonUpdate) => boolean;
    debounceMs?: number;
    throttleMs?: number;
  } = {},
): number {
  const [version, setVersion] = useState(0);
  const counts = useEffectEvent(
    (updates: SeasonUpdate[]) => !relevant || updates.length === 0 || updates.some(relevant),
  );
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const last = useRef(0);

  useEffect(() => {
    if (!seasonId) return;
    let first = true;
    const bump = () => {
      timer.current = null;
      last.current = Date.now();
      setVersion((v) => v + 1);
    };
    const stop = watchSeason(seasonId, (updates) => {
      if (first) {
        first = false;
        return;
      }
      if (!counts(updates)) return;
      if (debounceMs > 0) {
        if (timer.current) clearTimeout(timer.current);
        timer.current = setTimeout(bump, debounceMs);
        return;
      }
      if (throttleMs > 0) {
        if (timer.current) return;
        timer.current = setTimeout(bump, Math.max(0, last.current + throttleMs - Date.now()));
        return;
      }
      bump();
    });
    return () => {
      stop();
      if (timer.current) clearTimeout(timer.current);
      timer.current = null;
    };
  }, [seasonId, debounceMs, throttleMs]);

  return version;
}

/** The same for the pool (a game added, changed, deleted): skips the join, grows with every later change */
export function usePoolVersion(): number {
  const [version, setVersion] = useState(0);
  useEffect(() => {
    let first = true;
    return watchPool(() => {
      if (first) {
        first = false;
        return;
      }
      setVersion((v) => v + 1);
    });
  }, []);
  return version;
}
