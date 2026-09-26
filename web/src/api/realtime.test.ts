import { connectionStatus } from './connection';
import type { SeasonJoin, SeasonUpdate } from './realtime';

// E3, D-122: the client keeps the last sequence it saw, skips repeats, catches up after a gap or a lost connection —
// one catch-up at a time, a failed join again after a pause, the server's number after a restore.

type Handler = (...args: unknown[]) => void;
type Answer = SeasonJoin | Error | Promise<SeasonJoin>;

class FakeConnection {
  state = 'Connected';
  handlers = new Map<string, Handler>();
  reconnected: (() => void) | null = null;
  reconnecting: (() => void) | null = null;
  invoked: unknown[][] = [];
  answers: Answer[] = [];

  on(name: string, handler: Handler) {
    this.handlers.set(name, handler);
  }

  onreconnected(handler: () => void) {
    this.reconnected = handler;
  }

  onreconnecting(handler: () => void) {
    this.reconnecting = handler;
  }

  onclose(handler: () => void) {
    this.closed = handler;
  }

  starting: Promise<void> = Promise.resolve();
  closed: (() => void) | null = null;

  start() {
    return this.starting;
  }

  stop() {
    return Promise.resolve();
  }

  invoke(...args: unknown[]) {
    this.invoked.push(args);
    const answer = this.answers.shift() ?? { lastSequence: 0, missed: [], reload: false };
    return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
  }

  push(name: string, update?: SeasonUpdate) {
    this.handlers.get(name)?.(update);
  }
}

let connection = new FakeConnection();

vi.mock('@microsoft/signalr', () => ({
  LogLevel: { Warning: 3 },
  HubConnectionState: { Connected: 'Connected' },
  HubConnectionBuilder: class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return connection;
    }
  },
}));

const { watchPool, watchSeason } = await import('./realtime');

const update = (from: number, to: number): SeasonUpdate => ({
  seasonId: 's',
  fromSequence: from,
  toSequence: to,
  types: ['game-rolled'],
});

const joined = (lastSequence: number, missed: SeasonUpdate[] = [], reload = false): SeasonJoin => ({
  lastSequence,
  missed,
  reload,
});

async function watching(first: SeasonJoin) {
  connection.answers.push(first);
  const onChange = vi.fn();
  const stop = watchSeason('s', onChange);
  await vi.waitFor(() => {
    expect(onChange).toHaveBeenCalledTimes(1);
  });
  return { onChange, stop };
}

beforeEach(() => {
  connection = new FakeConnection();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('watchSeason', () => {
  it('joins, then passes each new update once and in order', async () => {
    const { onChange, stop } = await watching(joined(10));

    connection.push('seasonUpdated', update(11, 12));
    connection.push('seasonUpdated', update(11, 12));
    connection.push('seasonUpdated', update(9, 10));
    connection.push('seasonUpdated', update(13, 13));

    expect(connection.invoked[0]).toEqual(['Join', 's']);
    expect(onChange.mock.calls).toEqual([[[]], [[update(11, 12)]], [[update(13, 13)]]]);
    stop();
  });

  it('catches up from the last sequence after a gap and passes what was missed', async () => {
    const { onChange } = await watching(joined(10));

    connection.answers.push(joined(14, [update(11, 12), update(13, 14)]));
    connection.push('seasonUpdated', update(13, 14));
    await vi.waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(2);
    });

    expect(connection.invoked[1]).toEqual(['Resume', 's', 10]);
    expect(onChange).toHaveBeenLastCalledWith([update(11, 12), update(13, 14)]);
    connection.push('seasonUpdated', update(13, 14));
    expect(onChange).toHaveBeenCalledTimes(2);
  });

  it('tells the page when the live connection drops and when it is back', async () => {
    const { watchSeason } = await import('./realtime');
    const stop = watchSeason('s1', () => undefined);
    await vi.waitFor(() => {
      expect(connection.invoked.length).toBeGreaterThan(0);
    });
    expect(connectionStatus.get()).toBe('online');
    connection.reconnecting?.();
    expect(connectionStatus.get()).toBe('offline');
    connection.reconnected?.();
    expect(connectionStatus.get()).toBe('online');
    stop();
  });

  it('takes its «no connection» back when it stops, whatever it was doing', async () => {
    const { watchSeason } = await import('./realtime');
    // Dropped in a reconnect, then the page leaves: the mark goes with it
    const one = watchSeason('s1', () => undefined);
    await vi.waitFor(() => {
      expect(connection.invoked.length).toBeGreaterThan(0);
    });
    connection.reconnecting?.();
    expect(connectionStatus.get()).toBe('offline');
    one();
    expect(connectionStatus.get()).toBe('online');

    // A start that fails reports the loss; a stop while the start is still pending reports nothing after it
    connection = new FakeConnection();
    let fail: (reason: Error) => void = () => undefined;
    connection.starting = new Promise((_, reject) => {
      fail = reject;
    });
    const two = watchSeason('s2', () => undefined);
    two();
    fail(new Error('stopped before it started'));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(connectionStatus.get()).toBe('online');

    connection = new FakeConnection();
    connection.starting = Promise.reject(new Error('no server'));
    const three = watchSeason('s3', () => undefined);
    await vi.waitFor(() => {
      expect(connectionStatus.get()).toBe('offline');
    });
    three();
    expect(connectionStatus.get()).toBe('online');
  });

  it('is offline while any live subscription is', async () => {
    const { watchPool, watchSeason } = await import('./realtime');
    const season = watchSeason('s1', () => undefined);
    const seasonConnection = connection;
    connection = new FakeConnection();
    const pool = watchPool(() => undefined);
    const poolConnection = connection;
    await vi.waitFor(() => {
      expect(seasonConnection.invoked.length).toBeGreaterThan(0);
    });
    seasonConnection.reconnecting?.();
    poolConnection.reconnecting?.();
    seasonConnection.reconnected?.();
    expect(connectionStatus.get()).toBe('offline');
    poolConnection.reconnected?.();
    expect(connectionStatus.get()).toBe('online');
    season();
    pool();
  });

  it('refreshes after every reconnection, with what was missed if anything', async () => {
    const { onChange } = await watching(joined(10));

    connection.answers.push(joined(10));
    connection.reconnected?.();
    await vi.waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(2);
    });
    expect(connection.invoked[1]).toEqual(['Resume', 's', 10]);
    expect(onChange).toHaveBeenLastCalledWith([]);

    connection.answers.push(joined(12, [update(11, 12)]));
    connection.reconnected?.();
    await vi.waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(3);
    });
    expect(onChange).toHaveBeenLastCalledWith([update(11, 12)]);
  });

  it('takes the server number after a restore put the log behind the client', async () => {
    const { onChange } = await watching(joined(500));

    connection.answers.push(joined(40, [], true));
    connection.reconnected?.();
    await vi.waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(2);
    });

    connection.push('seasonUpdated', update(41, 41));
    expect(onChange).toHaveBeenLastCalledWith([update(41, 41)]);
  });

  it('keeps the numbers of a catch-up that listed past the last sequence', async () => {
    const { onChange } = await watching(joined(10));

    connection.answers.push(joined(11, [update(11, 11), update(12, 12)]));
    connection.push('seasonUpdated', update(12, 12));
    await vi.waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(2);
    });
    connection.push('seasonUpdated', update(12, 12));

    expect(onChange).toHaveBeenCalledTimes(2);
  });

  it('runs one catch-up at a time and one more for whatever asked meanwhile', async () => {
    const { onChange } = await watching(joined(10));
    let answer: (join: SeasonJoin) => void = () => undefined;
    connection.answers.push(new Promise<SeasonJoin>((resolve) => (answer = resolve)));
    connection.answers.push(joined(16, [update(15, 16)]));

    connection.push('seasonUpdated', update(13, 13));
    connection.push('seasonUpdated', update(14, 14));
    connection.push('seasonUpdated', update(15, 16));
    answer(joined(14, [update(11, 12), update(13, 14)]));
    await vi.waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(3);
    });

    expect(connection.invoked).toEqual([
      ['Join', 's'],
      ['Resume', 's', 10],
      ['Resume', 's', 14],
    ]);
    expect(onChange).toHaveBeenLastCalledWith([update(15, 16)]);
  });

  it('asks again after a failed join, and catches up an update that came before the answer', async () => {
    vi.useFakeTimers();
    connection.answers.push(new Error('database down'));
    const onChange = vi.fn();
    watchSeason('s', onChange);
    await vi.advanceTimersByTimeAsync(0);
    expect(onChange).not.toHaveBeenCalled();

    connection.answers.push(joined(10));
    await vi.advanceTimersByTimeAsync(5000);
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(connection.invoked.map((call) => call[0])).toEqual(['Join', 'Join']);
  });

  it('turns an update before the join into a join', async () => {
    let answer: (join: SeasonJoin) => void = () => undefined;
    connection.answers.push(new Promise<SeasonJoin>((resolve) => (answer = resolve)));
    const onChange = vi.fn();
    watchSeason('s', onChange);

    connection.answers.push(joined(11, [update(11, 11)]));
    connection.push('seasonUpdated', update(11, 11));
    answer(joined(10));
    await vi.waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(2);
    });

    expect(connection.invoked).toEqual([
      ['Join', 's'],
      ['Resume', 's', 10],
    ]);
    expect(onChange).toHaveBeenLastCalledWith([update(11, 11)]);
  });
});

describe('watchPool', () => {
  it('joins the pool and passes every change', async () => {
    const onChange = vi.fn();
    const stop = watchPool(onChange);
    await vi.waitFor(() => {
      expect(onChange).toHaveBeenCalledTimes(1);
    });

    connection.push('poolUpdated');

    expect(connection.invoked[0]).toEqual(['JoinPool']);
    expect(onChange).toHaveBeenCalledTimes(2);
    stop();
  });
});
