// The season the admin opened in «Сезон» (K1A): kept in this browser, so a reload or a new tab goes on with it. Browser
// storage may be missing or refuse (a private window): then the page simply starts on the current season.

const key = 'ge.admin.season';

export function keptSeason(): string | null {
  try {
    return globalThis.localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function keepSeason(seasonId: string | null) {
  try {
    if (seasonId) globalThis.localStorage.setItem(key, seasonId);
    else globalThis.localStorage.removeItem(key);
  } catch {
    // Not kept: the next visit starts on the current season
  }
}
