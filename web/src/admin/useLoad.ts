import { useCallback, useEffect, useEffectEvent, useState } from 'react';

export type Loaded<T> = (
  { status: 'loading' } | { status: 'failed' } | { status: 'ready'; data: T }
) & { reload: () => void };

/**
 * Loads a section's data. `version` changes when the season log moves on (another admin, a player's action): the data
 * is fetched again quietly, the old data stays on screen meanwhile and an open form keeps what is typed in it.
 */
export function useLoad<T>(
  load: () => Promise<T | undefined>,
  deps: readonly unknown[],
  version = 0,
): Loaded<T> {
  const [state, setState] = useState<
    { status: 'loading' | 'failed' } | { status: 'ready'; data: T }
  >({ status: 'loading' });
  const [again, setAgain] = useState(0);
  const fetchNow = useEffectEvent(load);

  // A change of what is loaded shows the skeleton again; a new version or a reload refreshes in place
  const key = JSON.stringify(deps);
  const [shownKey, setShownKey] = useState(key);
  if (shownKey !== key) {
    setShownKey(key);
    setState({ status: 'loading' });
  }

  useEffect(() => {
    let live = true;
    fetchNow()
      .then((data) => {
        if (!live) return;
        if (data === undefined) setState((s) => (s.status === 'ready' ? s : { status: 'failed' }));
        else setState({ status: 'ready', data });
      })
      .catch(() => {
        if (live) setState((s) => (s.status === 'ready' ? s : { status: 'failed' }));
      });
    return () => {
      live = false;
    };
  }, [key, version, again]);

  // After a failure the retry shows the skeleton again; ready data stays while it refreshes
  const reload = useCallback(() => {
    setState((s) => (s.status === 'failed' ? { status: 'loading' } : s));
    setAgain((n) => n + 1);
  }, []);

  return { ...state, reload };
}
