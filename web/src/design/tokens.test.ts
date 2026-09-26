/// <reference types="node" />
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { contrast, playerToken, tokenColors } from './players';

// Read as a file: the tests do not process CSS
const css = readFileSync(join(import.meta.dirname, 'tokens.css'), 'utf8');

// The tokens keep WCAG AA (docs/DESIGN.md «Цвет»): every text colour on the surface it is used on.

function token(name: string): string {
  const match = new RegExp(`--color-${name}:\\s*(#[0-9a-f]{6})`, 'i').exec(css);
  if (!match?.[1]) throw new Error(`No colour token --color-${name}`);
  return match[1];
}

describe('design tokens', () => {
  it.each([
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
    ['ink', 'gold'],
    ['ink', 'warning-soft'],
    ['ink', 'table'],
  ])('%s on %s reads at 4.5:1 or more', (text, surface) => {
    expect(contrast(token(text), token(surface))).toBeGreaterThanOrEqual(4.5);
  });

  it('has the same 16 player colours as src/design/players.ts', () => {
    const inCss = Array.from({ length: 16 }, (_, i) => token(`token-${i + 1}`).toLowerCase());
    expect(inCss).toEqual([...tokenColors]);
  });

  it('puts a readable letter on every player colour', () => {
    // The letter is large and heavy: AA for large text is 3:1; the better of ink and white gives 4:1 or more
    for (let i = 0; i < 16; i++) {
      const { hex, ink } = playerToken(i);
      const letter = ink === 'var(--color-ink)' ? token('ink') : token('on-color');
      expect(contrast(hex, letter), `token ${i + 1}`).toBeGreaterThanOrEqual(4);
    }
  });

  it('gives a player beyond 16 a colour again, from the start of the set', () => {
    expect(playerToken(16).fill).toBe(playerToken(0).fill);
    expect(playerToken(-1).fill).toBe(playerToken(15).fill);
  });
});
