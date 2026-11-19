import { useCallback, useSyncExternalStore } from 'react';

// A farther deadline must be checked again after the longest delay setTimeout accepts.
const maxTimeout = 2_147_483_647;

/** Whether the UTC instant has passed; re-renders when it does. */
export function useIsPast(instant: string | null): boolean {
  const at = instant === null ? NaN : Date.parse(instant);
  const subscribe = useCallback(
    (notify: () => void) => {
      if (!Number.isFinite(at)) return () => {};
      let timer: ReturnType<typeof setTimeout> | undefined;
      function check() {
        const remaining = at - Date.now();
        if (remaining <= 0) notify();
        else timer = setTimeout(check, Math.min(remaining, maxTimeout));
      }
      check();
      return () => {
        clearTimeout(timer);
      };
    },
    [at],
  );
  return useSyncExternalStore(subscribe, () => Date.now() >= at);
}
