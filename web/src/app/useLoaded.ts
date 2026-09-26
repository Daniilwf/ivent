import { useCallback, useEffect, useEffectEvent, useState } from 'react';

/** A page's data as it comes: loading, failed (retry), not found, or here */
export type Loaded<T> =
  { kind: 'loading' } | { kind: 'failed' } | { kind: 'notFound' } | { kind: 'ready'; value: T };

/** What one load answered: the data's state, or «the session is over» */
export type Answer<T> = Loaded<T> | { kind: 'signedOut' };

/** An API answer (openapi-fetch's `{ data, response }`) as a load's answer: 401 signs out, 404 is «not found» */
export function answerOf<T>({ data, response }: { data?: T; response: Response }): Answer<T> {
  if (data !== undefined) return { kind: 'ready', value: data };
  if (response.status === 401) return { kind: 'signedOut' };
  if (response.status === 404) return { kind: 'notFound' };
  return { kind: 'failed' };
}

/**
 * Loads a page's data with `load` (memoised by the caller: a new `load` is new data — the skeleton shows and it loads
 * again). A new `version` (the season log moved on: another player, another admin) loads again quietly: the data on
 * screen stays meanwhile, and a failed refresh keeps it. `reload` retries: after a failure the skeleton shows first.
 * An answer that comes after the page moved on is dropped. Without `onSignedOut` a signed-out answer is a failure.
 */
export function useLoaded<T>(
  load: () => Promise<Answer<T>>,
  { onSignedOut, version = 0 }: { onSignedOut?: () => void; version?: number } = {},
): Loaded<T> & { reload: () => void } {
  const [state, setState] = useState<Loaded<T>>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const signOut = useEffectEvent(() => onSignedOut?.());
  const onSignedOutSet = onSignedOut !== undefined;

  // Other data (a new load): the skeleton again, not the old page's data
  const [shown, setShown] = useState(() => load);
  if (shown !== load) {
    setShown(() => load);
    setState({ kind: 'loading' });
  }

  useEffect(() => {
    let active = true;
    const failed = (now: Loaded<T>): Loaded<T> => (now.kind === 'ready' ? now : { kind: 'failed' });
    load()
      .then((answer) => {
        if (!active) return;
        if (answer.kind === 'signedOut') {
          if (onSignedOutSet) signOut();
          else setState(failed);
        } else if (answer.kind === 'failed') setState(failed);
        else setState(answer);
      })
      .catch(() => {
        if (active) setState(failed);
      });
    return () => {
      active = false;
    };
  }, [load, version, attempt, onSignedOutSet]);

  const reload = useCallback(() => {
    setState((now) => (now.kind === 'ready' ? now : { kind: 'loading' }));
    setAttempt((n) => n + 1);
  }, []);

  return { ...state, reload };
}
