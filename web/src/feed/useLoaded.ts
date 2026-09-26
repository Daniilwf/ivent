import { useCallback, useEffect, useState } from 'react';

/** A page's data as it comes: loading, failed (retry), not found, or here */
export type Loaded<T> =
  { kind: 'loading' } | { kind: 'failed' } | { kind: 'notFound' } | { kind: 'ready'; value: T };

export type Answer<T> = Loaded<T> | { kind: 'signedOut' };

/**
 * Loads a page's data with `load` (memoised by the caller: a new `load` loads again) and gives a retry that shows the
 * loading state first. An answer that comes after the page moved on is dropped.
 */
export function useLoaded<T>(
  load: () => Promise<Answer<T>>,
  onSignedOut: () => void,
): [Loaded<T>, () => void] {
  const [state, setState] = useState<Loaded<T>>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let active = true;
    void load().then((answer) => {
      if (!active) return;
      if (answer.kind === 'signedOut') onSignedOut();
      else setState(answer);
    });
    return () => {
      active = false;
    };
  }, [load, attempt, onSignedOut]);

  const retry = useCallback(() => {
    setState({ kind: 'loading' });
    setAttempt((n) => n + 1);
  }, []);
  return [state, retry];
}
