import { playerToken } from '../design/players';
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
  const metrics = [
    card.completed,
    card.hours,
    ...card.ratings,
    ...card.attacks,
    ...(card.rolls ?? []),
  ];
  const glyphs = [
    card.player,
    card.season,
    card.standing?.place,
    card.standing?.points,
    ru.app.title,
    t.ratings,
    t.attacks,
    t.noRatings,
    t.rolls,
    t.noRolls,
    ...metrics.flatMap(({ label, value, unit, detail }) => [label, value, unit, detail]),
  ].join(' ');
  await Promise.allSettled(Object.values(font).map((face) => document.fonts.load(face, glyphs)));

  const space = Number.parseFloat(token('--spacing'));
  const ratio = 2;
  const width = 120 * space;
  const padding = 6 * space;
  const inner = width - 2 * padding;
  const seasonLines = linesOf(ctx, card.season, font.body, inner);
  const nameLines = linesOf(ctx, card.player, font.name, inner);
  const nameTop = padding + (seasonLines.length * 5 + 1) * space;
  const standingTop = nameTop + nameLines.length * 8 * space;
  const heroTop = standingTop + (card.standing ? 10 : 4) * space;
  const headline = [card.completed, card.hours];
  const heroWidth = inner - 10 * space;
  const sideBySide = headline.every(
    ({ value, unit }) => measure(value, font.hero) + unitRoom(unit) <= heroWidth / 2 - space,
  );
  const heroColumn = sideBySide ? heroWidth / 2 : heroWidth;
  const heroRow = 22 * space;
  const heroHeight = (sideBySide ? 1 : headline.length) * heroRow + 3 * space;
  const groupTop = heroTop + heroHeight + 5 * space;
  const rollsTop = groupTop + 25 * space;
  const bottom =
    card.rolls === null ? groupTop + 20 * space : rollsTop + (card.rolls.length ? 24 : 14) * space;
  const footerTop = bottom + 3 * space;
  const height = footerTop + 5 * space + padding;
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

  function measure(label: string, face: string) {
    ctx.font = face;
    return ctx.measureText(label).width;
  }

  function unitRoom(unit: string | undefined) {
    return unit ? space + measure(unit, font.body) : 0;
  }

  function metricValue(
    { value, unit }: RecapMetric,
    featured: boolean,
    x: number,
    y: number,
    cap: number,
  ) {
    const face = featured ? font.hero : font.number;
    const reserved = unitRoom(unit);
    const valueWidth = Math.min(measure(value, face), cap - reserved);
    text(value, face, featured ? white : ink, x, y, valueWidth);
    if (unit) {
      text(
        unit,
        font.body,
        featured ? white : soft,
        x + valueWidth + space,
        y + (featured ? 3 : 1) * space,
        reserved,
      );
    }
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
  nameLines.forEach((line, index) => {
    text(line, font.name, ink, padding, nameTop + index * 8 * space);
  });
  if (card.standing) {
    let x = padding;
    if (card.playerToken !== null) {
      const fill = playerToken(card.playerToken).fill;
      ctx.fillStyle = token(fill.slice(4, -1));
      ctx.beginPath();
      ctx.roundRect(x, standingTop + 2 * space, 8 * space, space, space / 2);
      ctx.fill();
      x += 10 * space;
    }
    text(card.standing.place, font.label, ink, x, standingTop);
    x += measure(card.standing.place, font.label) + 2 * space;
    text(card.standing.points, font.label, soft, x, standingTop);
  }

  ctx.fillStyle = token('--color-me');
  ctx.beginPath();
  ctx.roundRect(padding, heroTop, inner, heroHeight, innerRadius);
  ctx.fill();
  const heroX = padding + 5 * space;
  headline.forEach((metric, index) => {
    const x = heroX + (sideBySide ? index * heroColumn : 0);
    const y = heroTop + (sideBySide ? 0 : index * heroRow);
    text(metric.label, font.label, white, x, y + 4 * space, heroColumn - space);
    metricValue(metric, true, x, y + 10 * space, heroColumn - space);
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
  if (card.rolls !== null) {
    ctx.fillStyle = token('--color-muted');
    ctx.fillRect(padding, rollsTop - 3 * space, inner, 1);
    text(t.rolls, font.section, ink, padding, rollsTop);
    if (card.rolls.length) drawPair(card.rolls, padding, inner, rollsTop);
    else text(t.noRolls, font.body, soft, padding, rollsTop + 7 * space);
  }
  ctx.fillStyle = token('--color-muted');
  ctx.fillRect(padding, footerTop - 2 * space, inner, 1);
  text(ru.app.title, font.body, soft, padding, footerTop);

  function drawPair(metrics: RecapMetric[], start: number, width = groupWidth, top = groupTop) {
    const columnWidth = width / 2;
    metrics.forEach((metric, index) => {
      const x = start + index * columnWidth;
      const cap = columnWidth - space;
      text(metric.label, font.body, soft, x, top + 6 * space, cap);
      metricValue(metric, false, x, top + 11 * space, cap);
      if (metric.detail) text(metric.detail, font.body, soft, x, top + 18 * space, cap);
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
