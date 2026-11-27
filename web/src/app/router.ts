import { useEffect, useRef, useState, useSyncExternalStore } from 'react';

// The site's pages by address (H5, D-150; one router for every page, D-202): a few paths, so no router library. A
// link changes the address without reloading the page (Link.tsx); the browser's back and forward buttons work as usual.

export type Route =
  | { kind: 'season'; seasonId?: string }
  | { kind: 'feed'; seasonId: string | null }
  | { kind: 'gallery'; seasonId: string | null }
  | { kind: 'polls'; seasonId: string | null }
  | { kind: 'interactions'; seasonId: string | null }
  | { kind: 'profile'; userId: string }
  | { kind: 'seasonRecap'; seasonId: string }
  | { kind: 'hallOfFame' }
  | { kind: 'game'; gameId: string }
  | { kind: 'pool' }
  | { kind: 'rules' }
  | { kind: 'whatsNew' }
  /** The admin's pages (H8), for the admin only: App shows anyone else the game. `section` null is the proof queue */
  | { kind: 'admin'; section: string | null }
  | { kind: 'notFound' };

const id = '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})';
const season = new RegExp(`^/seasons/${id}$`);
const seasonFeed = new RegExp(`^/seasons/${id}/feed$`);
const seasonGallery = new RegExp(`^/seasons/${id}/gallery$`);
const seasonPolls = new RegExp(`^/seasons/${id}/polls$`);
const seasonInteractions = new RegExp(`^/seasons/${id}/interactions$`);
const seasonRecap = new RegExp(`^/seasons/${id}/recap$`);
const profile = new RegExp(`^/users/${id}$`);
const game = new RegExp(`^/games/${id}$`);
const admin = /^\/admin(?:\/([a-z]+))?$/;

/**
 * What page an address opens: `/`, `/seasons/{id}`, `/feed` (the current season), `/seasons/{id}/feed`, `/users/{id}`, `/games/{id}`,
 * `/pool`, `/rules`, `/hall-of-fame`, `/whats-new`, `/polls`, `/seasons/{id}/polls`, `/admin` and `/admin/{section}`
 */
export function routeOf(path: string): Route {
  const clean = path.replace(/\/+$/, '') || '/';
  if (clean === '/') return { kind: 'season' };
  if (clean === '/feed') return { kind: 'feed', seasonId: null };
  if (clean === '/gallery') return { kind: 'gallery', seasonId: null };
  if (clean === '/polls') return { kind: 'polls', seasonId: null };
  if (clean === '/interactions') return { kind: 'interactions', seasonId: null };
  if (clean === '/pool') return { kind: 'pool' };
  if (clean === '/rules') return { kind: 'rules' };
  if (clean === '/whats-new') return { kind: 'whatsNew' };
  if (clean === '/hall-of-fame') return { kind: 'hallOfFame' };
  let match = season.exec(clean);
  if (match?.[1]) return { kind: 'season', seasonId: match[1].toLowerCase() };
  match = seasonFeed.exec(clean);
  if (match?.[1]) return { kind: 'feed', seasonId: match[1] };
  match = seasonGallery.exec(clean);
  if (match?.[1]) return { kind: 'gallery', seasonId: match[1] };
  match = seasonPolls.exec(clean);
  if (match?.[1]) return { kind: 'polls', seasonId: match[1] };
  match = seasonInteractions.exec(clean);
  if (match?.[1]) return { kind: 'interactions', seasonId: match[1] };
  match = seasonRecap.exec(clean);
  if (match?.[1]) return { kind: 'seasonRecap', seasonId: match[1] };
  match = profile.exec(clean);
  if (match?.[1]) return { kind: 'profile', userId: match[1] };
  match = game.exec(clean);
  if (match?.[1]) return { kind: 'game', gameId: match[1] };
  match = admin.exec(clean);
  if (match) return { kind: 'admin', section: match[1] ?? null };
  return { kind: 'notFound' };
}

export const paths = {
  season: (seasonId?: string) => (seasonId ? `/seasons/${seasonId}` : '/'),
  feed: (seasonId?: string) => (seasonId ? `/seasons/${seasonId}/feed` : '/feed'),
  gallery: (seasonId?: string) => (seasonId ? `/seasons/${seasonId}/gallery` : '/gallery'),
  polls: (seasonId?: string) => (seasonId ? `/seasons/${seasonId}/polls` : '/polls'),
  interactions: (seasonId?: string) =>
    seasonId ? `/seasons/${seasonId}/interactions` : '/interactions',
  profile: (userId: string) => `/users/${userId}`,
  seasonRecap: (seasonId: string) => `/seasons/${seasonId}/recap`,
  hallOfFame: () => '/hall-of-fame',
  game: (gameId: string) => `/games/${gameId}`,
  pool: () => '/pool',
  rules: () => '/rules',
  whatsNew: () => '/whats-new',
  admin: (section?: string) => (section ? `/admin/${section}` : '/admin'),
};

const listeners = new Set<() => void>();

// A page that mounts after the address changed inside the site (a link, back or forward) was opened by the reader;
// the first page of a visit was not
let navigated = false;
// The current page was opened by Back or Forward: its entry keeps what it showed and where it was scrolled (K1A)
let popped = false;
globalThis.addEventListener('popstate', () => {
  navigated = true;
  popped = true;
  restoreEntryScroll();
});
// The site puts the scroll back itself: the browser would do it before a list loads (the pool and the feed do it again
// once their list is there, useRestoredScroll). Every page opened by Back or Forward goes where it was left, a page
// left without a kept place goes to the top, never where the page before it was scrolled (review of K1A-21)
if ('scrollRestoration' in globalThis.history) globalThis.history.scrollRestoration = 'manual';
const reloadScrollKey = 'scroll-on-reload';
// Chromium can commit a reload before a pagehide update to history.state reaches its new document.
globalThis.addEventListener('pagehide', () => {
  const { href } = globalThis.location;
  const y = globalThis.scrollY;
  try {
    globalThis.sessionStorage.setItem(reloadScrollKey, JSON.stringify([href, y]));
  } catch {
    // The history entry still remembers the position when storage is unavailable.
  }
  keepInEntry('scrollY', y);
});
if (isReload()) {
  popped = true;
  restoreReloadScroll();
  restoreEntryScroll();
}

function restoreReloadScroll() {
  try {
    const saved = globalThis.sessionStorage.getItem(reloadScrollKey);
    globalThis.sessionStorage.removeItem(reloadScrollKey);
    const entry: unknown = saved === null ? null : JSON.parse(saved);
    if (
      Array.isArray(entry) &&
      entry[0] === globalThis.location.href &&
      typeof entry[1] === 'number'
    ) {
      keepInEntry('scrollY', entry[1]);
    }
  } catch {
    // Fall back to the position stored in the history entry.
  }
}

function isReload(): boolean {
  // jsdom and old browsers may lack the navigation timing: then it is not known to be a reload
  try {
    const first = globalThis.performance.getEntriesByType('navigation')[0] as
      PerformanceNavigationTiming | undefined;
    return first?.type === 'reload';
  } catch {
    return false;
  }
}

/** After the page of the entry rendered, the scroll goes where the entry was left, or to the top */
function restoreEntryScroll() {
  const kept = entryState().scrollY;
  requestAnimationFrame(() => {
    if (typeof kept !== 'number' && globalThis.location.hash) {
      // With manual restoration, Forward to an anchor does not scroll it into view.
      document.querySelector(':target')?.scrollIntoView({ block: 'start' });
      return;
    }
    globalThis.scrollTo(0, typeof kept === 'number' ? kept : 0);
  });
}

/** What this history entry keeps: the scroll position and the values of `useEntryState` */
function entryState(): Record<string, unknown> {
  const state: unknown = globalThis.history.state;
  return typeof state === 'object' && state !== null ? (state as Record<string, unknown>) : {};
}

function keepInEntry(key: string, value: unknown) {
  globalThis.history.replaceState({ ...entryState(), [key]: value }, '');
}

/**
 * A page's value kept in its history entry (a search, the pages of a list shown): Back to the page brings it back,
 * a new visit by a link starts from `initial` (K1A). Without a `key` it is a plain state that the entry does not keep.
 */
export function useEntryState<T>(
  key: string | undefined,
  initial: T,
): [T, (next: T | ((now: T) => T)) => void] {
  const [value, setValue] = useState<T>(() => {
    if (key === undefined) return initial;
    const kept = entryState()[key];
    return popped && kept !== undefined ? (kept as T) : initial;
  });
  const set = (next: T | ((now: T) => T)) => {
    setValue((now) => {
      const resolved = typeof next === 'function' ? (next as (now: T) => T)(now) : next;
      if (key !== undefined) keepInEntry(key, resolved);
      return resolved;
    });
  };
  return [value, set];
}

/**
 * Back to a page: once its content is there (`ready`), the scroll goes where the reader left it, once (K1A). A page
 * opened by a link starts at the top as before.
 */
export function useRestoredScroll(ready: boolean) {
  const [restore] = useState(() => (popped ? entryState().scrollY : undefined));
  const done = useRef(false);
  useEffect(() => {
    if (!ready || done.current || typeof restore !== 'number') return;
    const frame = requestAnimationFrame(() => {
      done.current = true;
      globalThis.scrollTo(0, restore);
    });
    return () => {
      cancelAnimationFrame(frame);
    };
  }, [ready, restore]);
}

/** Tests only: the next page is the first of a new visit again (a test is a new visit, setup.ts) */
export function forgetNavigation() {
  navigated = false;
  popped = false;
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  globalThis.addEventListener('popstate', listener);
  return () => {
    listeners.delete(listener);
    globalThis.removeEventListener('popstate', listener);
  };
}

/** The current address's path; the page re-renders when a link or the back button changes it */
export function usePath(): string {
  return useSyncExternalStore(subscribe, () => globalThis.location.pathname);
}

const reopenListeners = new Set<() => void>();

/**
 * Called when a link opens the page already at the address (the header's link to the current section): whatever was
 * opened over it, like the password form, closes (K1A, D-150). Returns the unsubscribe.
 */
export function onReopen(listener: () => void): () => void {
  reopenListeners.add(listener);
  return () => {
    reopenListeners.delete(listener);
  };
}

/** Opens a page of the site without reloading it; a new page starts at the top */
export function navigate(to: string) {
  if (to === globalThis.location.pathname) {
    for (const listener of reopenListeners) listener();
    return;
  }
  // The page left keeps where it was scrolled, for Back
  keepInEntry('scrollY', globalThis.scrollY);
  globalThis.history.pushState(null, '', to);
  navigated = true;
  popped = false;
  globalThis.scrollTo(0, 0);
  for (const listener of listeners) listener();
}

/**
 * The page's heading (`tabIndex={-1}`): a page opened from inside the site gives it the focus, so a screen reader
 * starts reading at the new page and the keyboard goes on from there (H5–H8). `shown` is false while the heading is
 * not there yet (a page whose title comes with its data): it takes the focus when it comes, once. `page` names a part
 * of the page that has its own address (the admin's section): opening another one takes the focus again.
 */
export function usePageHeading<T extends HTMLElement = HTMLHeadingElement>(
  shown = true,
  page: string | null = null,
) {
  const heading = useRef<T>(null);
  const done = useRef<string | null | undefined>(undefined);
  useEffect(() => {
    if (done.current === page || !shown) return;
    if (!navigated) {
      done.current = page;
      return;
    }
    // On the next frame: a menu that opened the page lets go of the focus first
    const frame = requestAnimationFrame(() => {
      done.current = page;
      heading.current?.focus({ preventScroll: true });
    });
    return () => {
      cancelAnimationFrame(frame);
    };
  }, [shown, page]);
  return heading;
}
