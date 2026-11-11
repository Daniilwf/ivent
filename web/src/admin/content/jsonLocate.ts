// Where a field is in the JSON text the admin types (D-495): an error names its field by its path
// («effect.actions[0].steps»), and «Перейти к полю» selects that field in the editor. The text is scanned once into
// the place of every value; a field that is missing (a required one) points at the object that should hold it.

export type Place = { start: number; end: number };

const join = (path: string, key: string) => (path === '' ? key : `${path}.${key}`);

/** The place of every value of a JSON text by its path; null when the text is not JSON */
export function places(text: string): Map<string, Place> | null {
  const found = new Map<string, Place>();
  let i = 0;

  const space = () => {
    while (i < text.length && /\s/u.test(text[i] ?? '')) i++;
  };
  const fail = (): never => {
    throw new SyntaxError(`at ${i}`);
  };
  const literal = (word: string) => {
    if (text.startsWith(word, i)) i += word.length;
    else fail();
  };
  const string = (): string => {
    const start = i;
    if (text[i] !== '"') fail();
    i++;
    while (i < text.length && text[i] !== '"') i += text[i] === '\\' ? 2 : 1;
    if (i >= text.length) fail();
    i++;
    return JSON.parse(text.slice(start, i)) as string;
  };
  const value = (path: string) => {
    space();
    const start = i;
    const c = text[i];
    if (c === '{') {
      i++;
      space();
      if (text[i] === '}') i++;
      else
        for (;;) {
          space();
          const key = string();
          space();
          if (text[i] !== ':') fail();
          i++;
          value(join(path, key));
          space();
          if (text[i] === ',') i++;
          else if (text[i] === '}') {
            i++;
            break;
          } else fail();
        }
    } else if (c === '[') {
      i++;
      space();
      if (text[i] === ']') i++;
      else
        for (let n = 0; ; n++) {
          value(`${path}[${n}]`);
          space();
          if (text[i] === ',') i++;
          else if (text[i] === ']') {
            i++;
            break;
          } else fail();
        }
    } else if (c === '"') string();
    else if (c === 't') literal('true');
    else if (c === 'f') literal('false');
    else if (c === 'n') literal('null');
    else {
      const match = /^-?\d+(\.\d+)?([eE][-+]?\d+)?/u.exec(text.slice(i));
      if (!match) fail();
      i += match?.[0].length ?? 0;
    }
    found.set(path, { start, end: i });
  };

  try {
    value('');
    space();
    return i === text.length ? found : null;
  } catch {
    return null;
  }
}

/** The place of the field at `path`, or of the nearest enclosing value the text has */
export function locate(text: string, path: string): Place | null {
  const all = places(text);
  if (!all) return null;
  let at = path;
  for (;;) {
    const place = all.get(at);
    if (place) return place;
    if (at === '') return null;
    const cut = Math.max(at.lastIndexOf('.'), at.lastIndexOf('['));
    at = cut < 0 ? '' : at.slice(0, cut);
  }
}
