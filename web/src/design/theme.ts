import { useSyncExternalStore } from 'react';

/**
 * The theme (D-940): «Светлая», «Тёмная» or «Как в системе» (the default). A display preference of this device, kept in
 * localStorage, not game state. public/theme-init.js applies the stored choice before the first paint (the same key and
 * attribute); this module keeps it applied, follows the system while the choice is «Как в системе», and sets the
 * browser's theme-color from the page's colour.
 */
export type ThemeChoice = 'light' | 'dark' | 'system';
export type Theme = 'light' | 'dark';

export const themeChoices: readonly ThemeChoice[] = ['light', 'dark', 'system'];
/** The localStorage key; public/theme-init.js reads the same one */
export const themeStorageKey = 'ge.theme';
const darkQuery = '(prefers-color-scheme: dark)';

const listeners = new Set<() => void>();
let choice: ThemeChoice = readChoice();
let started = false;

function readChoice(): ThemeChoice {
  try {
    const stored = globalThis.localStorage.getItem(themeStorageKey);
    return stored === 'light' || stored === 'dark' ? stored : 'system';
  } catch {
    return 'system';
  }
}

function systemDark(): boolean {
  return typeof globalThis.matchMedia === 'function' && globalThis.matchMedia(darkQuery).matches;
}

/** The theme on screen now: the choice, or the system's when the choice is «Как в системе» */
export function currentTheme(): Theme {
  if (choice === 'system') return systemDark() ? 'dark' : 'light';
  return choice;
}

/** The page carries the choice as data-theme (none for «Как в системе»: the stylesheet then follows the system) */
function apply() {
  const root = document.documentElement;
  if (choice === 'system') delete root.dataset['theme'];
  else root.dataset['theme'] = choice;

  const scheme = document.querySelector<HTMLMetaElement>('meta[name="color-scheme"]');
  if (scheme) scheme.content = choice === 'system' ? 'light dark' : choice;

  // The browser's bar tracks the app tokens, or the canvas if the stylesheet failed to load.
  const bar = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
  const styles = getComputedStyle(root);
  const page =
    styles.getPropertyValue('--color-page').trim() ||
    styles.getPropertyValue('--initial-page-color').trim();
  if (bar && page) bar.content = page;
}

function notify() {
  for (const listener of listeners) listener();
}

/** Starts following the system's setting; called once when the page starts */
export function startTheme() {
  if (started) return;
  started = true;
  choice = readChoice();
  apply();
  if (typeof globalThis.matchMedia !== 'function') return;
  globalThis.matchMedia(darkQuery).addEventListener('change', () => {
    if (choice !== 'system') return;
    apply();
    notify();
  });
}

/** Keeps the choice on this device and shows it at once */
export function setThemeChoice(next: ThemeChoice) {
  choice = next;
  try {
    if (next === 'system') globalThis.localStorage.removeItem(themeStorageKey);
    else globalThis.localStorage.setItem(themeStorageKey, next);
  } catch {
    // Storage may be off (a private window): the choice still holds until the page is closed
  }
  apply();
  notify();
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The choice and the theme on screen, for the menu and for what draws with the theme's colours */
export function useTheme(): { choice: ThemeChoice; theme: Theme } {
  const current = useSyncExternalStore(subscribe, () => `${choice} ${currentTheme()}`);
  const [c, t] = current.split(' ') as [ThemeChoice, Theme];
  return { choice: c, theme: t };
}

/** For tests: forget the module's state as if the page had just been opened */
export function resetThemeForTests() {
  choice = readChoice();
  started = false;
  listeners.clear();
}
