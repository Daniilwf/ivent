import { useSyncExternalStore } from 'react';

// The site's pages by address (H5, D-150): a few paths, so no router library. A link changes the address without
// reloading the page (Link.tsx); the browser's back and forward buttons work as usual.

export type Route =
  | { kind: 'season' }
  | { kind: 'feed'; seasonId: string | null }
  | { kind: 'profile'; userId: string }
  | { kind: 'game'; gameId: string }
  | { kind: 'notFound' };

const id = '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})';
const seasonFeed = new RegExp(`^/seasons/${id}/feed$`);
const profile = new RegExp(`^/users/${id}$`);
const game = new RegExp(`^/games/${id}$`);

/** What page an address opens: `/`, `/feed` (the current season), `/seasons/{id}/feed`, `/users/{id}`, `/games/{id}` */
export function routeOf(path: string): Route {
  const clean = path.replace(/\/+$/, '') || '/';
  if (clean === '/') return { kind: 'season' };
  if (clean === '/feed') return { kind: 'feed', seasonId: null };
  let match = seasonFeed.exec(clean);
  if (match?.[1]) return { kind: 'feed', seasonId: match[1] };
  match = profile.exec(clean);
  if (match?.[1]) return { kind: 'profile', userId: match[1] };
  match = game.exec(clean);
  if (match?.[1]) return { kind: 'game', gameId: match[1] };
  return { kind: 'notFound' };
}

export const paths = {
  season: () => '/',
  feed: (seasonId?: string) => (seasonId ? `/seasons/${seasonId}/feed` : '/feed'),
  profile: (userId: string) => `/users/${userId}`,
  game: (gameId: string) => `/games/${gameId}`,
};

const listeners = new Set<() => void>();

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
  // jsdom has no scrolling
  if (!globalThis.navigator.userAgent.includes('jsdom')) globalThis.scrollTo(0, 0);
  for (const listener of listeners) listener();
}
