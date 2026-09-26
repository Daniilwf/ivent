import { useSyncExternalStore } from 'react';

// The site has a handful of pages and no router: the address is read from the location, and a page changes it with
// navigate(), which tells the subscribers. The browser's back and forward buttons work through popstate.

const CHANGED = 'site:navigate';

function subscribe(changed: () => void) {
  globalThis.addEventListener('popstate', changed);
  globalThis.addEventListener(CHANGED, changed);
  return () => {
    globalThis.removeEventListener('popstate', changed);
    globalThis.removeEventListener(CHANGED, changed);
  };
}

const snapshot = () => globalThis.location.pathname;

/** The current path, kept up to date */
export function usePath(): string {
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

/** Opens another page of the site without reloading it */
export function navigate(path: string) {
  if (globalThis.location.pathname === path) return;
  globalThis.history.pushState(null, '', path);
  globalThis.dispatchEvent(new Event(CHANGED));
}
