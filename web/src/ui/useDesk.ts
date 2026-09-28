import { useSyncExternalStore } from 'react';

// The desktop layout starts at the `desk` breakpoint of the tokens (900px)
const query = '(min-width: 900px)';

function subscribe(changed: () => void) {
  if (typeof window.matchMedia !== 'function') return () => undefined;
  const media = window.matchMedia(query);
  media.addEventListener('change', changed);
  return () => {
    media.removeEventListener('change', changed);
  };
}

const snapshot = () => typeof window.matchMedia === 'function' && window.matchMedia(query).matches;

/** Whether the page has the desktop layout: a moment plays on the map's stage there, in the turn card on a phone */
export function useDesk(): boolean {
  return useSyncExternalStore(subscribe, snapshot, () => false);
}
