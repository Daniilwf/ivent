import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
import type { Schemas } from './client';

export type SeasonUpdate = Schemas['SeasonUpdate'];
export type SeasonJoin = Schemas['SeasonJoin'];
export type PoolUpdate = Schemas['PoolUpdate'];

const retryDelayMs = 5000;

function connect(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl('/hubs/season')
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => retryDelayMs })
    .configureLogging(LogLevel.Warning)
    .build();
}

/** Starts the connection, retrying for as long as the page is open, then runs `ready`. */
function keepConnected(
  connection: HubConnection,
  ready: () => Promise<void>,
  stopped: () => boolean,
) {
  const start = async () => {
    while (!stopped()) {
      try {
        await connection.start();
        await ready();
        return;
      } catch {
        await new Promise((resolve) => setTimeout(resolve, retryDelayMs));
      }
    }
  };

  connection.onreconnected(() => void ready());
  connection.onclose(() => {
    if (!stopped()) void start();
  });
  void start();
}

/**
 * Subscribes to updates of one season (E3, D-122). The client keeps the last sequence of the season log it saw: an update
 * it already saw is skipped; a gap (an update that starts past the next sequence) or a lost connection makes it resume
 * from that sequence, and the server lists what was missed. `onChange` runs once per new update, after the first join,
 * and after a resume that found anything missed. Returns an unsubscribe function.
 */
export function watchSeason(
  seasonId: string,
  onChange: (updates: SeasonUpdate[]) => void,
): () => void {
  let stopped = false;
  let last: number | null = null;
  const connection = connect();

  const apply = (join: SeasonJoin, first: boolean) => {
    const fresh = join.missed.filter((u) => last === null || u.toSequence > last);
    last = Math.max(last ?? 0, join.lastSequence);
    if (first || join.reload || fresh.length > 0) onChange(fresh);
  };

  const resume = async () => {
    try {
      if (last === null) {
        apply(await connection.invoke<SeasonJoin>('Join', seasonId), true);
      } else {
        apply(await connection.invoke<SeasonJoin>('Resume', seasonId, last), false);
      }
    } catch {
      // The next reconnect resumes again.
    }
  };

  connection.on('seasonUpdated', (update: SeasonUpdate) => {
    if (last === null || update.toSequence <= last) return;
    if (update.fromSequence > last + 1) {
      // Something between was lost on the way: the log has it
      void resume();
      return;
    }
    last = update.toSequence;
    onChange([update]);
  });

  keepConnected(connection, resume, () => stopped);
  return () => {
    stopped = true;
    void connection.stop();
  };
}

/** Subscribes to changes of the pool and the category wheel (D-122); `onChange` also runs after every (re)join. */
export function watchPool(onChange: () => void): () => void {
  let stopped = false;
  const connection = connect();
  const join = async () => {
    try {
      await connection.invoke('JoinPool');
      onChange();
    } catch {
      // The next reconnect joins again.
    }
  };

  connection.on('poolUpdated', () => {
    onChange();
  });
  keepConnected(connection, join, () => stopped);
  return () => {
    stopped = true;
    void connection.stop();
  };
}
