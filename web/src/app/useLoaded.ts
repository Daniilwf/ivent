import { useCallback, useEffect, useEffectEvent, useState } from 'react';

/** A rate limit gives more useful retry guidance than a lost connection (K1A). */
type LoadFailure = { kind: 'failed'; tooOften?: boolean };
type Refresh = { kind: 'loading' } | { kind: 'ready' } | LoadFailure;

/** A page's data as it comes: loading, failed (retry), not found, or here */
export type Loaded<T> =
  { kind: 'loading' } | LoadFailure | { kind: 'notFound' } | { kind: 'ready'; value: T };

/** What one load answered: the data's state, or «the session is over» */
export type Answer<T> = Loaded<T> | { kind: 'signedOut' };

/** An API answer (openapi-fetch's `{ data, response }`) as a load's answer: 401 signs out, 404 is «not found» */
export function answerOf<T>({ data, response }: { data?: T; response: Response }): Answer<T> {
  if (data !== undefined) return { kind: 'ready', value: data };
  if (response.status === 401) return { kind: 'signedOut' };
  if (response.status === 404) return { kind: 'notFound' };
  if (response.status === 429) return { kind: 'failed', tooOften: true };
  return { kind: 'failed' };
}

/**
 * Loads a page's data with `load` (memoised by the caller: a new `load` is new data — the skeleton shows and it loads
 * again). A new `version` (the season log moved on: another player, another admin) loads again quietly: the data on
 * screen stays meanwhile, and a failed refresh keeps it. `reload` retries: after a failure the skeleton shows first.
 * An answer that comes after the page moved on is dropped. Without `onSignedOut` a signed-out answer is a failure.
 * `refresh` describes the latest requested read even when the screen still shows the last successful data.
 */
export function useLoaded<T>(
  load: () => Promise<Answer<T>>,
  { onSignedOut, version = 0 }: { onSignedOut?: () => void; version?: number } = {},
): Loaded<T> & { reload: () => void; refresh: Refresh } {
  const [state, setState] = useState<Loaded<T>>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [request, setRequest] = useState(() => ({ load, version, attempt }));
  const [settled, setSettled] = useState<{ request: typeof request; refresh: Refresh }>();
  const signOut = useEffectEvent(() => onSignedOut?.());
  const onSignedOutSet = onSignedOut !== undefined;

  // Other data (a new load): the skeleton again, not the old page's data
  if (request.load !== load || request.version !== version || request.attempt !== attempt) {
    setRequest({ load, version, attempt });
    if (request.load !== load) setState({ kind: 'loading' });
  }

  useEffect(() => {
    let active = true;
    const settle = (answer: Loaded<T>) => {
      setState((now) => (answer.kind === 'failed' && now.kind === 'ready' ? now : answer));
      const refresh: Refresh =
        answer.kind === 'failed' || answer.kind === 'loading' ? answer : { kind: 'ready' };
      setSettled({ request, refresh });
    };
    request
      .load()
      .then((answer) => {
        if (!active) return;
        if (answer.kind === 'signedOut') {
          if (onSignedOutSet) signOut();
          else settle({ kind: 'failed' });
        } else settle(answer);
      })
      .catch(() => {
        if (active) settle({ kind: 'failed' });
      });
    return () => {
      active = false;
    };
  }, [request, onSignedOutSet]);

  const reload = useCallback(() => {
    setState((now) => (now.kind === 'ready' ? now : { kind: 'loading' }));
    setAttempt((n) => n + 1);
  }, []);

  const refresh: Refresh = settled?.request === request ? settled.refresh : { kind: 'loading' };
  return { ...state, reload, refresh };
}
