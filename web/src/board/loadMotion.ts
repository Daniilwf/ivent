// The animation engine (motion) is the largest part of the page's script after React, and the first screen moves
// nothing: it loads apart, right after the page starts, so the map shows within the budget on a mobile network
// (DESIGN.md «Производительность», FL-3). The components draw with `m` (the light part) inside `Motion`; a moment
// waits for `loadMotion` before it plays, which by then has long arrived.

let loading: ReturnType<typeof load> | null = null;

function load() {
  return import('./motionFeatures');
}

/** The animation engine, once per page; a failed download is tried again by the next asker */
export function loadMotion() {
  loading ??= load().catch((error: unknown) => {
    loading = null;
    throw error;
  });
  return loading;
}
