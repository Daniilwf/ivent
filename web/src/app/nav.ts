import { useCallback, useSyncExternalStore } from 'react';

/** The signed-in sections of the site, each at its own address (H6, H7) */
export type Page = 'home' | 'pool' | 'rules';

export const pagePaths: Record<Page, string> = { home: '/', pool: '/pool', rules: '/rules' };

/** The section of an address; an unknown one is the main page */
export function pageOf(pathname: string): Page {
  const path = pathname.replace(/\/+$/, '') || '/';
  const found = (Object.keys(pagePaths) as Page[]).find((page) => pagePaths[page] === path);
  return found ?? 'home';
}

const changed = 'site:navigate';

function subscribe(notify: () => void) {
  globalThis.addEventListener('popstate', notify);
  globalThis.addEventListener(changed, notify);
  return () => {
    globalThis.removeEventListener('popstate', notify);
    globalThis.removeEventListener(changed, notify);
  };
}

/** Opens a section without reloading the page; the browser's back button returns */
export function navigate(page: Page) {
  if (pageOf(globalThis.location.pathname) === page) return;
  globalThis.history.pushState(null, '', pagePaths[page]);
  globalThis.scrollTo(0, 0);
  globalThis.dispatchEvent(new Event(changed));
}

/** The section the address shows now, and a way to open another */
export function usePage(): [Page, (page: Page) => void] {
  const page = useSyncExternalStore(subscribe, () => pageOf(globalThis.location.pathname));
  const go = useCallback((next: Page) => {
    navigate(next);
  }, []);
  return [page, go];
}
