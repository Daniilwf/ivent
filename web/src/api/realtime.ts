import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr';
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
 * from that sequence, and the server lists what was missed. One catch-up runs at a time; whatever asks for another while
 * it runs gets one more after it. A failed join or catch-up is tried again after a pause. `onChange` runs once per new
 * update, after the first join, after every reconnection (names and avatars live outside the season log) and after a
 * catch-up that found anything. Returns an unsubscribe function.
 */
export function watchSeason(
  seasonId: string,
  onChange: (updates: SeasonUpdate[]) => void,
): () => void {
  let stopped = false;
  let last: number | null = null;
  const pending = { running: false, again: false, refresh: false };
  // Set by other callbacks while a catch-up awaits the server: read through a call, not narrowed away
  const askedAgain = () => pending.again;
  const connection = connect();

  const apply = (join: SeasonJoin, refresh: boolean) => {
    const fresh = join.missed.filter((u) => last === null || u.toSequence > last);
    if (join.reload || last === null) {
      // The server knows best: after a restore the log is behind what the client saw
      last = join.lastSequence;
    } else {
      last = Math.max(last, join.lastSequence, ...join.missed.map((u) => u.toSequence));
    }
    if (refresh || join.reload || fresh.length > 0) onChange(fresh);
  };

  const catchUp = async (refresh: boolean): Promise<void> => {
    pending.refresh ||= refresh;
    if (pending.running) {
      pending.again = true;
      return;
    }
    pending.running = true;
    try {
      do {
        pending.again = false;
        const first = last === null;
        const join =
          last === null
            ? await connection.invoke<SeasonJoin>('Join', seasonId)
            : await connection.invoke<SeasonJoin>('Resume', seasonId, last);
        const refreshNow = first || pending.refresh;
        pending.refresh = false;
        apply(join, refreshNow);
      } while (askedAgain() && !stopped);
    } catch {
      // Not joined or not caught up: try again after a pause, unless the connection is gone (its reconnect resumes)
      pending.running = false;
      if (!stopped) {
        setTimeout(() => {
          if (!stopped && connection.state === HubConnectionState.Connected) void catchUp(false);
        }, retryDelayMs);
      }
      return;
    }
    pending.running = false;
  };

  connection.on('seasonUpdated', (update: SeasonUpdate) => {
    if (last === null || pending.running) {
      // Not joined yet, or a catch-up is on its way: it lists this update too
      void catchUp(false);
      return;
    }
    if (update.toSequence <= last) return;
    if (update.fromSequence > last + 1) {
      // Something between was lost on the way: the log has it
      void catchUp(false);
      return;
    }
    last = update.toSequence;
    onChange([update]);
  });

  keepConnected(
    connection,
    () => catchUp(true),
    () => stopped,
  );
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
