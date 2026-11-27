/// <reference types="node" />
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { camera, mapLabel } from '../board/camera';
import { eventDraw, wheelMotion } from './motion';
import { contrast, playerToken, tokenColors, tokenLetters } from './players';

// Read as a file: the tests do not process CSS
const css = readFileSync(join(import.meta.dirname, 'tokens.css'), 'utf8');

/** The declarations of one block of tokens.css: from its opening line to its first closing brace */
function block(opening: string): string {
  const start = css.indexOf(opening);
  if (start < 0) throw new Error(`No block «${opening}» in tokens.css`);
  return css.slice(start + opening.length, css.indexOf('}', start));
}

/** name → value of every `--name: value;` in a block */
function declarations(text: string): Map<string, string> {
  return new Map(
    [...text.matchAll(/(--[a-z0-9-]+):\s*([^;]+);/gi)].map((m) => [
      m[1] ?? '',
      (m[2] ?? '').trim(),
    ]),
  );
}

const lightTheme = declarations(block('@theme static {'));
const darkTheme = declarations(block(":root[data-theme='dark'] {"));
const systemDark = declarations(block(":root:not([data-theme='light']) {"));
const themes = { light: lightTheme, dark: new Map([...lightTheme, ...darkTheme]) };

// The tokens keep WCAG AA (docs/DESIGN.md «Цвет»): every text colour on the surface it is used on, in both themes.

function token(name: string, theme: Map<string, string> = themes.light): string {
  const value = theme.get(`--color-${name}`);
  if (!value || !/^#[0-9a-f]{6}$/i.test(value)) throw new Error(`No colour token --color-${name}`);
  return value;
}

describe('design tokens', () => {
  const pairs = [
    ['ink', 'page'],
    ['ink', 'card'],
    ['ink-soft', 'page'],
    ['ink-soft', 'card'],
    ['on-color', 'action'],
    ['on-color', 'action-strong'],
    ['on-color', 'me'],
    ['on-color', 'danger'],
    ['danger', 'card'],
    ['danger', 'danger-soft'],
    ['success', 'success-soft'],
    ['warning', 'warning-soft'],
    ['info', 'info-soft'],
    // Text and marks on gold and on the zone colours are `on-bright`, dark in both themes (DT-3, DT-4)
    ['on-bright', 'gold'],
    ['ink', 'warning-soft'],
    ['ink', 'table'],
    // The dark theme turns `bg-ink` into a light fill: the current section and a picked option keep reading
    ['on-color', 'ink'],
    ['card', 'ink'],
    ['on-selected', 'selected'],
    ['ink-soft', 'muted'],
    ['ink', 'muted'],
    ['on-bright', 'zone-meadow'],
    ['on-bright', 'zone-forest'],
    ['on-bright', 'zone-mountains'],
    ['on-bright', 'zone-swamp'],
    ['on-bright', 'zone-city'],
    ['on-bright', 'zone-castle'],
  ] as const;

  it.each(pairs)('%s on %s reads at 4.5:1 or more in the light theme', (text, surface) => {
    expect(contrast(token(text), token(surface))).toBeGreaterThanOrEqual(4.5);
  });

  it.each(pairs)('%s on %s reads at 4.5:1 or more in the dark theme', (text, surface) => {
    expect(contrast(token(text, themes.dark), token(surface, themes.dark))).toBeGreaterThanOrEqual(
      4.5,
    );
  });

  it.each([
    ['ink', 'card'],
    ['ink', 'table'],
    ['me', 'table'],
    ['me', 'card'],
    ['action', 'card'],
    ['route', 'table'],
  ])('%s as an outline or a mark on %s keeps 3:1 in the dark theme', (mark, surface) => {
    expect(contrast(token(mark, themes.dark), token(surface, themes.dark))).toBeGreaterThanOrEqual(
      3,
    );
  });

  it('gives every colour token of the light theme a dark value, except the colours that stay', () => {
    const stays = /^--color-(token-\d+|on-token-light)$/;
    const colours = [...lightTheme.keys()].filter(
      (name) => name.startsWith('--color-') && name !== '--color-*' && !stays.test(name),
    );
    expect(colours.length).toBeGreaterThan(25);
    for (const name of colours) expect(darkTheme.has(name), name).toBe(true);
    for (const name of darkTheme.keys()) expect(lightTheme.has(name), name).toBe(true);
  });

  it('applies the same dark values for «Тёмная» and for «Как в системе» on a dark system', () => {
    expect([...systemDark]).toEqual([...darkTheme]);
    expect(css).toMatch(
      /@media \(prefers-color-scheme: dark\) \{\s*:root:not\(\[data-theme='light'\]\) \{/,
    );
  });

  it('keeps the existing 16 player fills in both themes', () => {
    expect(token('token-6')).toBe('#d55e00');
    for (const theme of Object.values(themes)) {
      const inCss = Array.from({ length: 16 }, (_, i) => token(`token-${i + 1}`, theme));
      expect(inCss).toEqual([...tokenColors]);
    }
  });

  it('keeps the light letters and uses a readable letter on every dark token', () => {
    expect(token('on-token-dark')).toBe(tokenLetters.dark);
    expect(token('on-token-light')).toBe(tokenLetters.light);
    for (let i = 0; i < 16; i++) {
      const { hex, ink } = playerToken(i);
      const letter = ink === 'var(--color-on-token-dark)' ? 'on-token-dark' : 'on-token-light';
      // Preserve the vermilion light sticker (DT-1), even where its established letter falls short of AA.
      expect(contrast(hex, token(letter)), `light token ${i + 1}`).toBeGreaterThanOrEqual(
        i === 5 ? 4.1 : 4.5,
      );
      expect(
        contrast(hex, token(letter, themes.dark)),
        `dark token ${i + 1}`,
      ).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('keeps the original light map glyphs and makes them readable on the dark current cell', () => {
    expect(token('on-bright')).toBe(token('ink'));
    expect(
      contrast(token('on-bright', themes.dark), token('me', themes.dark)),
    ).toBeGreaterThanOrEqual(4.5);
    expect(lightTheme.get('--color-on-current-fork')).toBe('var(--color-on-color)');
    expect(darkTheme.get('--color-on-current-fork')).toBe('var(--color-ink)');
  });

  it('overrides React Flow colours only for the dark editor', () => {
    const flow = declarations(block('.react-flow.dark {'));
    expect(flow.get('--xy-background-color')).toBe('var(--color-page)');
    expect(flow.get('--xy-controls-button-color')).toBe('var(--color-ink)');
    expect(css).not.toMatch(/\.react-flow\s*\{/);
  });

  it('has the same smallest map label as src/board/camera.ts, and a close-up that keeps it', () => {
    expect(css).toContain(`--map-label-min: ${String(mapLabel.min)}px;`);
    expect(camera.closeUp * mapLabel.world).toBeGreaterThanOrEqual(mapLabel.min);
  });

  it('has the same event draw duration and easing as the transition used by Motion', () => {
    expect(css).toContain(
      `--duration-event-draw: ${String(eventDraw.transition.duration * 1000)}ms;`,
    );
    expect(css).toContain(`--ease-out: cubic-bezier(${eventDraw.transition.ease.join(', ')});`);
  });

  it('reveals a mobile roll result at the same pace as the wheel card', () => {
    expect(css).toContain(
      `--duration-wheel-result: ${String(wheelMotion.reveal.duration * 1000)}ms;`,
    );
  });

  it('gives a player beyond 16 a colour again, from the start of the set', () => {
    expect(playerToken(16).fill).toBe(playerToken(0).fill);
    expect(playerToken(-1).fill).toBe(playerToken(15).fill);
  });
});
