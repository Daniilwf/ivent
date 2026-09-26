// The state of the live connection, apart from realtime.ts: pages read it, tests that fake realtime keep it

/** Whether the live updates reach the page: the shell shows a quiet mark while they do not (DESIGN.md) */
export type ConnectionStatus = 'online' | 'offline';

let status: ConnectionStatus = 'online';
const listeners = new Set<() => void>();

export function reportConnection(next: ConnectionStatus) {
  if (status === next) return;
  status = next;
  for (const listener of listeners) listener();
}

export const connectionStatus = {
  get: (): ConnectionStatus => status,
  subscribe: (listener: () => void) => {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
};
