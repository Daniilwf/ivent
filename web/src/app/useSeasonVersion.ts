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
 * - `refreshOnJoin` — also refresh on a late initial join if an earlier HTTP snapshot could miss updates.
 * - `snapshotSequence` — refresh once if the first snapshot trails the initial join, with `null` while loading.
 */
export function useSeasonVersion(
  seasonId: string | null,
  {
    relevant,
    debounceMs = 0,
    throttleMs = 0,
    refreshOnJoin = false,
    snapshotSequence,
  }: {
    relevant?: (update: SeasonUpdate) => boolean;
    debounceMs?: number;
    throttleMs?: number;
    refreshOnJoin?: boolean;
    snapshotSequence?: number | null;
  } = {},
): number {
  const [version, setVersion] = useState(0);
  const counts = useEffectEvent(
    (updates: SeasonUpdate[]) => !relevant || updates.length === 0 || updates.some(relevant),
  );
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const last = useRef(0);
  const initialJoinSequence = useRef<number | null>(null);
  const trackSnapshot = snapshotSequence !== undefined;
  const compareFirstSnapshot = useEffectEvent(() => {
    if (snapshotSequence == null || initialJoinSequence.current === null) return;
    const behind = snapshotSequence < initialJoinSequence.current;
    initialJoinSequence.current = null;
    if (behind) setVersion((v) => v + 1);
  });

  useEffect(() => {
    if (!seasonId) return;
    let first = true;
    let subscribing = true;
    const bump = () => {
      timer.current = null;
      last.current = Date.now();
      setVersion((v) => v + 1);
    };
    const onChange = (updates: SeasonUpdate[]) => {
      if (first) {
        first = false;
        // A synchronous initial callback precedes the consumer's load effect and needs no second read.
        if (!refreshOnJoin || subscribing) return;
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
    };
    const onInitialJoin = (sequence: number) => {
      initialJoinSequence.current = sequence;
      compareFirstSnapshot();
    };
    const stop = watchSeason(seasonId, onChange, trackSnapshot ? onInitialJoin : undefined);
    subscribing = false;
    return () => {
      stop();
      initialJoinSequence.current = null;
      if (timer.current) clearTimeout(timer.current);
      timer.current = null;
    };
  }, [seasonId, debounceMs, throttleMs, refreshOnJoin, trackSnapshot]);

  useEffect(() => {
    compareFirstSnapshot();
  }, [snapshotSequence]);

  return version;
}

/** The same for the pool (a game added, changed, deleted): skips the join, grows with every later change */
export function usePoolVersion(observe = true): number {
  const [version, setVersion] = useState(0);
  useEffect(() => {
    if (!observe) return;
    let first = true;
    return watchPool(() => {
      if (first) {
        first = false;
        return;
      }
      setVersion((v) => v + 1);
    });
  }, [observe]);
  return version;
}
