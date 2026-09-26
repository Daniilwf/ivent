// The state of the live connection, apart from realtime.ts: pages read it, tests that fake realtime keep it.
// Each live subscription (a season, the pool) registers and reports for itself; the page is offline while any of them
// is, and a subscription that stops takes its report with it, so «no connection» never outlives the page that had it.

/** Whether the live updates reach the page: the shell shows a quiet mark while they do not (DESIGN.md) */
export type ConnectionStatus = 'online' | 'offline';

const offline = new Set<symbol>();
const listeners = new Set<() => void>();
let status: ConnectionStatus = 'online';

function update() {
  const next: ConnectionStatus = offline.size > 0 ? 'offline' : 'online';
  if (next === status) return;
  status = next;
  for (const listener of listeners) listener();
}

/** One live subscription's voice: it reports its own state and releases it when it stops */
export function registerConnection() {
  const id = Symbol('connection');
  return {
    report(next: ConnectionStatus) {
      if (next === 'offline') offline.add(id);
      else offline.delete(id);
      update();
    },
    release() {
      offline.delete(id);
      update();
    },
  };
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
