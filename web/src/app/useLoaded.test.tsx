import { act, renderHook, waitFor } from '@testing-library/react';
import { answerOf, useLoaded, type Answer } from './useLoaded';

// One loader for every page (D-202): the feed's pages, the rules and the admin's sections.

const ready = <T,>(value: T): Answer<T> => ({ kind: 'ready', value });

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

describe('useLoaded', () => {
  it('shows the loading state first, then the data', async () => {
    const load = () => Promise.resolve(ready(1));
    const { result } = renderHook(() => useLoaded(load));

    expect(result.current.kind).toBe('loading');
    await waitFor(() => {
      expect(result.current).toMatchObject({ kind: 'ready', value: 1 });
    });
  });

  it('refreshes quietly on a new version and keeps the data when the refresh fails', async () => {
    let answer: Answer<number> = ready(1);
    const load = vi.fn(() => Promise.resolve(answer));
    const { result, rerender } = renderHook(({ version }) => useLoaded(load, { version }), {
      initialProps: { version: 0 },
    });
    await waitFor(() => {
      expect(result.current.kind).toBe('ready');
    });

    answer = ready(2);
    rerender({ version: 1 });
    expect(result.current).toMatchObject({ kind: 'ready', value: 1 });
    await waitFor(() => {
      expect(result.current).toMatchObject({ kind: 'ready', value: 2 });
    });

    answer = { kind: 'failed' };
    rerender({ version: 2 });
    await waitFor(() => {
      expect(load).toHaveBeenCalledTimes(3);
    });
    expect(result.current).toMatchObject({ kind: 'ready', value: 2 });
  });

  it('shows the skeleton again for other data (a new load)', async () => {
    const { result, rerender } = renderHook(({ load }) => useLoaded(load), {
      initialProps: { load: () => Promise.resolve(ready('a')) },
    });
    await waitFor(() => {
      expect(result.current.kind).toBe('ready');
    });

    const next = deferred<Answer<string>>();
    rerender({ load: () => next.promise });
    expect(result.current.kind).toBe('loading');
    await act(async () => {
      next.resolve(ready('b'));
      await next.promise;
    });
    expect(result.current).toMatchObject({ kind: 'ready', value: 'b' });
  });

  it('drops an answer that comes after the page moved on', async () => {
    const old = deferred<Answer<string>>();
    const { result, rerender } = renderHook(({ load }) => useLoaded(load), {
      initialProps: { load: () => old.promise },
    });
    rerender({ load: () => Promise.resolve(ready('new')) });
    await waitFor(() => {
      expect(result.current).toMatchObject({ kind: 'ready', value: 'new' });
    });

    await act(async () => {
      old.resolve(ready('old'));
      await old.promise;
    });
    expect(result.current).toMatchObject({ kind: 'ready', value: 'new' });
  });

  it('retries after a failure through the loading state', async () => {
    let answer: Answer<number> = { kind: 'failed' };
    const load = () => Promise.resolve(answer);
    const { result } = renderHook(() => useLoaded(load));
    await waitFor(() => {
      expect(result.current.kind).toBe('failed');
    });

    answer = ready(3);
    act(() => {
      result.current.reload();
    });
    expect(result.current.kind).toBe('loading');
    await waitFor(() => {
      expect(result.current).toMatchObject({ kind: 'ready', value: 3 });
    });
  });

  it('signs out on a signed-out answer, or fails without a way to sign out', async () => {
    const load = () => Promise.resolve<Answer<number>>({ kind: 'signedOut' });
    const onSignedOut = vi.fn();
    renderHook(() => useLoaded(load, { onSignedOut }));
    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalledOnce();
    });

    const { result } = renderHook(() => useLoaded(load));
    await waitFor(() => {
      expect(result.current.kind).toBe('failed');
    });
  });

  it('turns a thrown load into a failure', async () => {
    const load = () => Promise.reject(new Error('offline'));
    const { result } = renderHook(() => useLoaded(load));
    await waitFor(() => {
      expect(result.current.kind).toBe('failed');
    });
  });
});

describe('answerOf', () => {
  it('reads an API answer: the data, 401 signs out, 404 is not found, anything else fails', () => {
    const response = (status: number) => new Response(null, { status });
    expect(answerOf({ data: 5, response: response(200) })).toEqual({ kind: 'ready', value: 5 });
    expect(answerOf({ response: response(401) })).toEqual({ kind: 'signedOut' });
    expect(answerOf({ response: response(404) })).toEqual({ kind: 'notFound' });
    expect(answerOf({ response: response(500) })).toEqual({ kind: 'failed' });
  });
});
