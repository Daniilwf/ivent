import { useEffect, useRef, useSyncExternalStore } from 'react';

// The site's pages by address (H5, D-150; one router for every page, D-202): a few paths, so no router library. A
// link changes the address without reloading the page (Link.tsx); the browser's back and forward buttons work as usual.

export type Route =
  | { kind: 'season' }
  | { kind: 'feed'; seasonId: string | null }
  | { kind: 'profile'; userId: string }
  | { kind: 'game'; gameId: string }
  | { kind: 'pool' }
  | { kind: 'rules' }
  /** The admin's pages (H8), for the admin only: App shows anyone else the game. `section` null is the proof queue */
  | { kind: 'admin'; section: string | null }
  | { kind: 'notFound' };

const id = '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})';
const seasonFeed = new RegExp(`^/seasons/${id}/feed$`);
const profile = new RegExp(`^/users/${id}$`);
const game = new RegExp(`^/games/${id}$`);
const admin = /^\/admin(?:\/([a-z]+))?$/;

/**
 * What page an address opens: `/`, `/feed` (the current season), `/seasons/{id}/feed`, `/users/{id}`, `/games/{id}`,
 * `/pool`, `/rules`, `/admin` and `/admin/{section}`
 */
export function routeOf(path: string): Route {
  const clean = path.replace(/\/+$/, '') || '/';
  if (clean === '/') return { kind: 'season' };
  if (clean === '/feed') return { kind: 'feed', seasonId: null };
  if (clean === '/pool') return { kind: 'pool' };
  if (clean === '/rules') return { kind: 'rules' };
  let match = seasonFeed.exec(clean);
  if (match?.[1]) return { kind: 'feed', seasonId: match[1] };
  match = profile.exec(clean);
  if (match?.[1]) return { kind: 'profile', userId: match[1] };
  match = game.exec(clean);
  if (match?.[1]) return { kind: 'game', gameId: match[1] };
  match = admin.exec(clean);
  if (match) return { kind: 'admin', section: match[1] ?? null };
  return { kind: 'notFound' };
}

export const paths = {
  season: () => '/',
  feed: (seasonId?: string) => (seasonId ? `/seasons/${seasonId}/feed` : '/feed'),
  profile: (userId: string) => `/users/${userId}`,
  game: (gameId: string) => `/games/${gameId}`,
  pool: () => '/pool',
  rules: () => '/rules',
  admin: (section?: string) => (section ? `/admin/${section}` : '/admin'),
};

const listeners = new Set<() => void>();

// A page that mounts after the address changed inside the site (a link, back or forward) was opened by the reader;
// the first page of a visit was not
let navigated = false;
globalThis.addEventListener('popstate', () => {
  navigated = true;
});

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

/** Opens a page of the site without reloading it; a new page starts at the top */
export function navigate(to: string) {
  if (to === globalThis.location.pathname) return;
  globalThis.history.pushState(null, '', to);
  navigated = true;
  // jsdom has no scrolling
  if (!globalThis.navigator.userAgent.includes('jsdom')) globalThis.scrollTo(0, 0);
  for (const listener of listeners) listener();
}

/**
 * The page's heading (`tabIndex={-1}`): a page opened from inside the site gives it the focus, so a screen reader
 * starts reading at the new page and the keyboard goes on from there (H5–H8). `shown` is false while the heading is
 * not there yet (a page whose title comes with its data): it takes the focus when it comes, once.
 */
export function usePageHeading<T extends HTMLElement = HTMLHeadingElement>(shown = true) {
  const heading = useRef<T>(null);
  const done = useRef(false);
  useEffect(() => {
    if (done.current || !shown) return;
    if (!navigated) {
      done.current = true;
      return;
    }
    // On the next frame: a menu that opened the page lets go of the focus first
    const frame = requestAnimationFrame(() => {
      done.current = true;
      heading.current?.focus({ preventScroll: true });
    });
    return () => {
      cancelAnimationFrame(frame);
    };
  }, [shown]);
  return heading;
}
