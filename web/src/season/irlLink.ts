// Limits.MaxProofLinkLength of the engine (D-680); the season config controls pictures separately.
export const maxIrlLinkLength = 500;

/** Match .NET's whitespace trim without dropping format characters such as a leading BOM. */
export function trimIrlLink(value: string): string {
  return value.replace(/^\p{White_Space}+|\p{White_Space}+$/gu, '');
}

/** Inspect an address without fetching it. Keep the original trimmed spelling for the submitted proof. */
export function parseIrlLink(value: string): URL | null {
  const link = trimIrlLink(value);
  if (
    link.length > maxIrlLinkLength ||
    /[\\\p{White_Space}\p{Cc}\p{Cf}]/u.test(link) ||
    !/^https:\/\/[^/]/iu.test(link)
  )
    return null;
  try {
    const url = new URL(link);
    return url.protocol === 'https:' &&
      url.hostname !== '' &&
      url.username === '' &&
      url.password === ''
      ? url
      : null;
  } catch {
    return null;
  }
}
