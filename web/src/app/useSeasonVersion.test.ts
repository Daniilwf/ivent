import { act, renderHook } from '@testing-library/react';
import type { SeasonUpdate } from '../api/realtime';
import { useSeasonVersion } from './useSeasonVersion';

// One way for every page to follow the season's log (D-202): the join is skipped, a burst is one read.

let notify: (updates: SeasonUpdate[]) => void = () => undefined;
const stopped = vi.fn();

vi.mock('../api/realtime', () => ({
  watchSeason: (_: string, onChange: (updates: SeasonUpdate[]) => void) => {
    notify = onChange;
    return stopped;
  },
}));

const update = (...types: string[]): SeasonUpdate => ({
  seasonId: 's',
  fromSequence: 1,
  toSequence: 1,
  types,
});

describe('useSeasonVersion', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('skips the join and grows with every later update', () => {
    const { result } = renderHook(() => useSeasonVersion('s'));
    act(() => {
      notify([]);
    });
    expect(result.current).toBe(0);

    act(() => {
      notify([update('game-rolled')]);
      notify([]);
    });
    expect(result.current).toBe(2);
  });

  it('counts only the relevant updates, and a catch-up without a list', () => {
    const { result } = renderHook(() =>
      useSeasonVersion('s', { relevant: (u) => u.types.includes('ruleset-changed') }),
    );
    act(() => {
      notify([]);
      notify([update('game-rolled')]);
    });
    expect(result.current).toBe(0);
    act(() => {
      notify([update('ruleset-changed')]);
      notify([]);
    });
    expect(result.current).toBe(2);
  });

  it('reads once after a burst when debounced', () => {
    vi.useFakeTimers();
    const { result } = renderHook(() => useSeasonVersion('s', { debounceMs: 300 }));
    act(() => {
      notify([]);
      notify([update('a')]);
      vi.advanceTimersByTime(200);
      notify([update('b')]);
      vi.advanceTimersByTime(200);
    });
    expect(result.current).toBe(0);
    act(() => {
      vi.advanceTimersByTime(100);
    });
    expect(result.current).toBe(1);
  });

  it('reads at most once per period when throttled', () => {
    vi.useFakeTimers();
    const { result } = renderHook(() => useSeasonVersion('s', { throttleMs: 10_000 }));
    act(() => {
      notify([]);
      notify([update('a')]);
    });
    act(() => {
      vi.advanceTimersByTime(0);
    });
    expect(result.current).toBe(1);
    act(() => {
      notify([update('b')]);
      notify([update('c')]);
      vi.advanceTimersByTime(5_000);
    });
    expect(result.current).toBe(1);
    act(() => {
      vi.advanceTimersByTime(5_000);
    });
    expect(result.current).toBe(2);
  });

  it('follows nothing without a season and stops with the page', () => {
    const { result, unmount } = renderHook(({ id }) => useSeasonVersion(id), {
      initialProps: { id: 's' },
    });
    unmount();
    expect(stopped).toHaveBeenCalled();
    expect(result.current).toBe(0);
  });
});
