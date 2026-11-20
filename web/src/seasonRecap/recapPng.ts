import { ru } from '../i18n/ru';
import type { RecapCard, RecapMetric } from './recapCard';

const t = ru.seasonRecap;

export async function downloadRecap(card: RecapCard) {
  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d');
  if (!context) throw new Error('Canvas context unavailable');
  const ctx = context;

  const theme = getComputedStyle(document.documentElement);
  const token = (name: string) => theme.getPropertyValue(name).trim();
  const fontText = token('--font-text');
  const fontDisplay = token('--font-display');
  const font = {
    body: `400 ${token('--text-sm')} ${fontText}`,
    label: `600 ${token('--text-sm')} ${fontText}`,
    name: `800 ${token('--text-xl')} ${fontDisplay}`,
    hero: `800 ${token('--text-2xl')} ${fontDisplay}`,
    section: `800 ${token('--text-sm')} ${fontDisplay}`,
    number: `800 ${token('--text-lg')} ${fontDisplay}`,
  };
  const glyphs = `${card.player} ${card.season} ${t.noRatings} ${t.completed} ${t.hours} ${t.attacksReceived} 0123456789,—`;
  await Promise.allSettled(Object.values(font).map((face) => document.fonts.load(face, glyphs)));

  const space = Number.parseFloat(token('--spacing'));
  const ratio = 2;
  const width = 120 * space;
  const padding = 6 * space;
  const inner = width - 2 * padding;
  const seasonLines = linesOf(ctx, card.season, font.body, inner);
  const nameLines = linesOf(ctx, card.player, font.name, inner);
  const heroTop = padding + (seasonLines.length * 5 + nameLines.length * 8 + 5) * space;
  const headline = [card.completed, card.hours];
  const heroWidth = inner - 10 * space;
  ctx.font = font.hero;
  const sideBySide = headline.every(
    ({ value }) => ctx.measureText(value).width <= heroWidth / 2 - space,
  );
  const heroColumn = sideBySide ? heroWidth / 2 : heroWidth;
  const heroRow = 22 * space;
  const heroHeight = (sideBySide ? 1 : headline.length) * heroRow + 3 * space;
  const groupTop = heroTop + heroHeight + 5 * space;
  const height = groupTop + 20 * space + padding;
  canvas.width = width * ratio;
  canvas.height = height * ratio;
  ctx.scale(ratio, ratio);
  ctx.textBaseline = 'top';

  const ink = token('--color-ink');
  const soft = token('--color-ink-soft');
  const white = token('--color-on-color');
  const radius = Number.parseFloat(token('--radius-lg'));
  const innerRadius = Number.parseFloat(token('--radius-md'));

  function text(
    label: string,
    face: string,
    color: string,
    x: number,
    y: number,
    maxWidth = inner,
  ) {
    ctx.font = face;
    ctx.fillStyle = color;
    ctx.fillText(label, x, y, maxWidth);
  }

  ctx.fillStyle = token('--color-card');
  ctx.strokeStyle = ink;
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.roundRect(1, 1, width - 2, height - 2, radius);
  ctx.fill();
  ctx.stroke();

  seasonLines.forEach((line, index) => {
    text(line, font.body, soft, padding, padding + index * 5 * space);
  });
  const nameTop = padding + (seasonLines.length * 5 + 1) * space;
  nameLines.forEach((line, index) => {
    text(line, font.name, ink, padding, nameTop + index * 8 * space);
  });

  ctx.fillStyle = token('--color-me');
  ctx.beginPath();
  ctx.roundRect(padding, heroTop, inner, heroHeight, innerRadius);
  ctx.fill();
  const heroX = padding + 5 * space;
  headline.forEach(({ value, label }, index) => {
    const x = heroX + (sideBySide ? index * heroColumn : 0);
    const y = heroTop + (sideBySide ? 0 : index * heroRow);
    text(value, font.hero, white, x, y + 4 * space, heroColumn - space);
    text(label, font.label, white, x, y + 17 * space, heroColumn - space);
  });

  const groupWidth = inner / 2 - 4 * space;
  const right = padding + inner - groupWidth;
  const divider = padding + inner / 2;
  ctx.fillStyle = token('--color-muted');
  ctx.fillRect(divider, groupTop, 1, 20 * space);
  text(t.ratings, font.section, ink, padding, groupTop);
  text(t.attacks, font.section, ink, right, groupTop);
  if (card.ratings.length) drawPair(card.ratings, padding);
  else text(t.noRatings, font.body, soft, padding, groupTop + 7 * space, groupWidth);
  drawPair(card.attacks, right);

  function drawPair(metrics: RecapMetric[], start: number) {
    const columnWidth = groupWidth / 2;
    metrics.forEach(({ label, value, unit }, index) => {
      const x = start + index * columnWidth;
      const cap = columnWidth - space;
      const valueTop = groupTop + 7 * space;
      text(value, font.number, ink, x, valueTop, cap);
      if (unit) {
        const unitX = x + ctx.measureText(value).width + space;
        text(unit, font.body, soft, unitX, valueTop + space, x + cap - unitX);
      }
      text(label, font.body, soft, x, groupTop + 13 * space, cap);
    });
  }

  const blob = await new Promise<Blob | null>((resolve) => {
    canvas.toBlob(resolve, 'image/png');
  });
  if (!blob) throw new Error('PNG encoding failed');
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.download = t.filename;
  link.href = url;
  document.body.append(link);
  try {
    link.click();
  } finally {
    link.remove();
    // Keep the blob readable while the browser starts the download.
    globalThis.setTimeout(() => {
      URL.revokeObjectURL(url);
    }, 30_000);
  }
}

function linesOf(ctx: CanvasRenderingContext2D, text: string, font: string, maxWidth: number) {
  const graphemes = new Intl.Segmenter('ru', { granularity: 'grapheme' });
  const lines: string[] = [];
  ctx.font = font;
  let line = '';

  for (const word of text.split(/\s+/)) {
    const next = line ? `${line} ${word}` : word;
    if (ctx.measureText(next).width <= maxWidth) {
      line = next;
      continue;
    }

    if (line) lines.push(line);
    line = '';
    for (const { segment } of graphemes.segment(word)) {
      if (line && ctx.measureText(line + segment).width > maxWidth) {
        lines.push(line);
        line = '';
      }
      line += segment;
    }
  }
  if (line) lines.push(line);
  return lines;
}
