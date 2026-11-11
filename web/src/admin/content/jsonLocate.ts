// Where a field is in the JSON text the admin types (D-495): an error names its field by its path
// («effect.actions[0].steps»), and «Перейти к полю» selects that field in the editor. The text is scanned once into
// the place of every value; a field that is missing (a required one) points at the object that should hold it.
// The same scan says where a text stops being JSON (line and character), so the editor never shows the browser's
// English message (invariant 13).

export type Place = { start: number; end: number };

const join = (path: string, key: string) => (path === '' ? key : `${path}.${key}`);

/** Where a text stops being JSON: the offset of the first character JSON does not allow, and its line and column (from 1) */
export type JsonFault = { offset: number; line: number; column: number };

class Fault extends Error {
  readonly offset: number;

  constructor(offset: number) {
    super(`at ${offset}`);
    this.offset = offset;
  }
}

/** The place of every value of a JSON text by its path, or the offset where it stops being JSON */
function scan(text: string): Map<string, Place> | Fault {
  const found = new Map<string, Place>();
  let i = 0;

  const space = () => {
    // JSON's own whitespace only: a no-break space is not one
    while (i < text.length && ' \t\n\r'.includes(text[i] ?? 'x')) i++;
  };
  const fail = (): never => {
    throw new Fault(i);
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
    try {
      return JSON.parse(text.slice(start, i)) as string;
    } catch {
      // A raw control character or a bad escape inside: the string is where the text breaks
      i = start;
      return fail();
    }
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
      const match = /^-?(0|[1-9]\d*)(\.\d+)?([eE][-+]?\d+)?/u.exec(text.slice(i));
      if (!match) fail();
      i += match?.[0].length ?? 0;
    }
    found.set(path, { start, end: i });
  };

  try {
    value('');
    space();
    return i === text.length ? found : new Fault(i);
  } catch (e) {
    if (e instanceof Fault) return e;
    throw e;
  }
}

/** The place of every value of a JSON text by its path; null when the text is not JSON */
export function places(text: string): Map<string, Place> | null {
  const result = scan(text);
  return result instanceof Fault ? null : result;
}

/** Where the text stops being JSON, or null when it is JSON */
export function jsonFault(text: string): JsonFault | null {
  try {
    JSON.parse(text);
    return null;
  } catch {
    const result = scan(text);
    // The scan follows JSON.parse; should they ever disagree, the end of the text is the place
    const offset = result instanceof Fault ? result.offset : text.length;
    const before = text.slice(0, offset).split('\n');
    return { offset, line: before.length, column: (before.at(-1)?.length ?? 0) + 1 };
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
