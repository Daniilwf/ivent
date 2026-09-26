// The 16 player token colours (docs/DESIGN.md «Цвет»): the same list as --color-token-N in tokens.css, which a test
// keeps equal. The map and the stickers fill with the CSS token; this list only picks the letter colour on each.

export const tokenColors = [
  '#e69f00',
  '#56b4e9',
  '#009e73',
  '#f0e442',
  '#0072b2',
  '#d55e00',
  '#cc79a7',
  '#3b3b3b',
  '#9a6a00',
  '#1f6f9f',
  '#006c4f',
  '#b8a800',
  '#004a75',
  '#8f3f00',
  '#8c4a73',
  '#8a8a8a',
] as const;

const ink = '#1e2030';
const white = '#ffffff';

function luminance(hex: string) {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrast(a: string, b: string) {
  const [x, y] = [luminance(a), luminance(b)];
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}

/** A player's token by their number in the season (0-based): the fill and the letter colour on it */
export function playerToken(index: number) {
  const n = ((index % tokenColors.length) + tokenColors.length) % tokenColors.length;
  const color = tokenColors[n] as string;
  const onColor =
    contrast(color, white) >= contrast(color, ink) ? 'var(--color-on-color)' : 'var(--color-ink)';
  return { fill: `var(--color-token-${n + 1})`, ink: onColor, hex: color };
}

/** A stable token colour for a user outside a season (the header, a profile, a game page): by their id */
export function userToken(id: string) {
  let hash = 0;
  for (const ch of id) hash = (hash * 31 + ch.charCodeAt(0)) | 0;
  return Math.abs(hash) % tokenColors.length;
}
