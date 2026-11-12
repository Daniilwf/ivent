/**
 * The first character of a name as the reader sees it (a grapheme): a whole emoji sequence, a letter with its marks.
 * The avatar's stand-in shows it; the first UTF-16 unit would be half of an emoji (K1A).
 */
export function initial(name: string): string {
  const trimmed = name.trim();
  const first = new Intl.Segmenter('ru', { granularity: 'grapheme' })
    .segment(trimmed)
    [Symbol.iterator]()
    .next();
  return first.done ? '' : first.value.segment;
}
