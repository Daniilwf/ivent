import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import type { Schemas } from './client';

export type SeasonUpdate = Schemas['SeasonUpdate'];

const retryDelayMs = 5000;

/**
 * Subscribes to updates of one season. `onChange` runs after every committed command of the season and
 * after every (re)join, so whatever was committed while not listening is picked up too.
 * The connection keeps retrying for as long as the page is open. Returns an unsubscribe function.
 */
export function watchSeason(seasonId: string, onChange: () => void): () => void {
  let stopped = false;
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/season')
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => retryDelayMs })
    .configureLogging(LogLevel.Warning)
    .build();

  const join = async () => {
    try {
      await connection.invoke('Join', seasonId);
      onChange();
    } catch {
      // The next reconnect joins again.
    }
  };

  const start = async () => {
    while (!stopped) {
      try {
        await connection.start();
        await join();
        return;
      } catch {
        await new Promise((resolve) => setTimeout(resolve, retryDelayMs));
      }
    }
  };

  connection.on('seasonUpdated', onChange);
  connection.onreconnected(() => void join());
  connection.onclose(() => {
    if (!stopped) void start();
  });
  void start();

  return () => {
    stopped = true;
    void connection.stop();
  };
}
