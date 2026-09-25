import type { SeasonJoin, SeasonUpdate } from './realtime';

// E3, D-122: the client keeps the last sequence it saw, skips repeats, resumes after a gap or a lost connection.

type Handler = (...args: unknown[]) => void;

class FakeConnection {
  handlers = new Map<string, Handler>();
  reconnected: (() => void) | null = null;
  invoked: unknown[][] = [];
  answers: SeasonJoin[] = [];

  on(name: string, handler: Handler) {
    this.handlers.set(name, handler);
  }

  onreconnected(handler: () => void) {
    this.reconnected = handler;
  }

  onclose() {
    // Not used here
  }

  start() {
    return Promise.resolve();
  }

  stop() {
    return Promise.resolve();
  }

  invoke(...args: unknown[]) {
    this.invoked.push(args);
    return Promise.resolve(this.answers.shift() ?? { lastSequence: 0, missed: [], reload: false });
  }

  push(update: SeasonUpdate) {
    this.handlers.get('seasonUpdated')?.(update);
  }
}

let connection = new FakeConnection();

vi.mock('@microsoft/signalr', () => ({
  LogLevel: { Warning: 3 },
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

const { watchSeason } = await import('./realtime');

const update = (from: number, to: number): SeasonUpdate => ({
  seasonId: 's',
  fromSequence: from,
  toSequence: to,
  types: ['game-rolled'],
});

async function settle() {
  for (let i = 0; i < 5; i++) await Promise.resolve();
}

beforeEach(() => {
  connection = new FakeConnection();
});

describe('watchSeason', () => {
  it('joins, then passes each new update once and in order', async () => {
    connection.answers.push({ lastSequence: 10, missed: [], reload: false });
    const onChange = vi.fn();
    const stop = watchSeason('s', onChange);
    await settle();

    connection.push(update(11, 12));
    connection.push(update(11, 12));
    connection.push(update(9, 10));
    connection.push(update(13, 13));

    expect(connection.invoked[0]).toEqual(['Join', 's']);
    expect(onChange.mock.calls).toEqual([[[]], [[update(11, 12)]], [[update(13, 13)]]]);
    stop();
  });

  it('resumes from the last sequence after a gap and passes what was missed', async () => {
    connection.answers.push({ lastSequence: 10, missed: [], reload: false });
    const onChange = vi.fn();
    watchSeason('s', onChange);
    await settle();

    connection.answers.push({
      lastSequence: 14,
      missed: [update(11, 12), update(13, 14)],
      reload: false,
    });
    connection.push(update(13, 14));
    await settle();

    expect(connection.invoked[1]).toEqual(['Resume', 's', 10]);
    expect(onChange).toHaveBeenLastCalledWith([update(11, 12), update(13, 14)]);
    connection.push(update(13, 14));
    expect(onChange).toHaveBeenCalledTimes(2);
  });

  it('resumes after a lost connection and stays quiet when nothing was missed', async () => {
    connection.answers.push({ lastSequence: 10, missed: [], reload: false });
    const onChange = vi.fn();
    watchSeason('s', onChange);
    await settle();

    connection.answers.push({ lastSequence: 10, missed: [], reload: false });
    connection.reconnected?.();
    await settle();
    expect(connection.invoked[1]).toEqual(['Resume', 's', 10]);
    expect(onChange).toHaveBeenCalledTimes(1);

    connection.answers.push({ lastSequence: 500, missed: [], reload: true });
    connection.reconnected?.();
    await settle();
    expect(onChange).toHaveBeenCalledTimes(2);

    connection.push(update(501, 501));
    expect(onChange).toHaveBeenLastCalledWith([update(501, 501)]);
  });
});
