// The content editor's model (4.12): the pack in force as plain JSON, the parts of the content schema the editor
// checks one object against, problems by the path of their field, and the pack after one object is saved or removed.

import { ru } from '../../i18n/ru';
import {
  checkSchema,
  isSchema,
  variantType,
  type JsonSchema,
  type SchemaProblem,
} from '../ruleSchema';
import { jsonFault, type JsonFault } from './jsonLocate';
import type { Json } from './templates';

const t = ru.admin.content;

export type Pack = { objects: Json[]; wheels: Json[] };

/** What the editor has open: an object or a wheel of the pack by its place, or a new one (index null) */
export type Collection = 'objects' | 'wheels';

/** The pack the server sent: anything that is not a list of objects reads as empty */
export function packOf(value: unknown): Pack {
  const record = isSchema(value) ? (value as Record<string, unknown>) : {};
  const list = (key: string) =>
    Array.isArray(record[key])
      ? (record[key] as unknown[]).filter((x): x is Json => isSchema(x))
      : [];
  return { objects: list('objects'), wheels: list('wheels') };
}

export type ContentSchemas = {
  object: JsonSchema | null;
  wheel: JsonSchema | null;
  /** The schema of one base action: the variants by `type` */
  action: JsonSchema | null;
};

const at = (schema: JsonSchema | boolean | undefined, key: string): JsonSchema | null => {
  if (!isSchema(schema)) return null;
  const property = schema.properties?.[key];
  return isSchema(property) ? property : null;
};

const items = (schema: JsonSchema | null): JsonSchema | null =>
  schema && isSchema(schema.items) ? schema.items : null;

/** The schemas of an object, a wheel and an action inside the schema of a pack */
export function contentSchemas(pack: JsonSchema | null): ContentSchemas {
  const object = items(at(pack ?? undefined, 'objects'));
  return {
    object,
    wheel: items(at(pack ?? undefined, 'wheels')),
    action: items(at(at(object ?? undefined, 'effect') ?? undefined, 'actions')),
  };
}

/** The `type` of each variant of the action schema */
export const actionVariants = (action: JsonSchema | null): unknown[] =>
  (action?.anyOf ?? []).map(variantType).filter((x) => x !== undefined);

/** A problem of the open object: the path of its field inside the object ('' — the object itself) and what is wrong */
export type Problem = { path: string; text: string; source: 'schema' | 'server' };

const typeWords = (types: string[]) =>
  types.map((x) => ru.admin.rules.types[x] ?? x).join(ru.admin.rules.or);

/** The schema's problem in words, for the field it is about */
export function schemaProblem(problem: SchemaProblem): Problem {
  const path = problem.path === '$' ? '' : problem.path;
  switch (problem.kind) {
    case 'required':
      return { path, text: t.problem.required, source: 'schema' };
    case 'unknown':
      return { path, text: t.problem.unknown, source: 'schema' };
    case 'pattern':
      return { path, text: t.problem.pattern, source: 'schema' };
    case 'length':
      return { path, text: t.problem.length(problem.min ?? 0, problem.max), source: 'schema' };
    case 'range':
      return { path, text: t.problem.range(problem.min, problem.max), source: 'schema' };
    case 'type':
      return { path, text: t.problem.type(typeWords(problem.expected)), source: 'schema' };
    case 'enum':
      return {
        path,
        text: t.problem.enum(problem.values.map((v) => JSON.stringify(v)).join(', ')),
        source: 'schema',
      };
  }
}

/** The server's English message (D-492) in Russian where the dictionary knows it, else as it came */
export function serverText(message: string): string {
  const known = t.serverMessages[message];
  if (known) return known;
  for (const [pattern, say] of t.serverPatterns) {
    const match = pattern.exec(message);
    if (match) return say(match[1] ?? '');
  }
  return t.serverSays(message);
}

/** The server's problems of the pack split into those of the open item («objects[3]…» → its own path) and the rest */
export function serverProblems(
  problems: readonly { path: string; message: string }[],
  prefix: string | null,
): { own: Problem[]; other: { path: string; text: string }[] } {
  const own: Problem[] = [];
  const other: { path: string; text: string }[] = [];
  for (const problem of problems) {
    const text = serverText(problem.message);
    if (prefix !== null && (problem.path === prefix || problem.path.startsWith(`${prefix}.`)))
      own.push({ path: problem.path.slice(prefix.length + 1), text, source: 'server' });
    else other.push({ path: problem.path, text });
  }
  return { own, other };
}

/** What the typed text of the open item is: not JSON, or a value with the schema's problems */
export function inspect(
  text: string,
  schema: JsonSchema | null,
): { parse: JsonFault | null; value: Json | null; problems: Problem[] } {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    // Where it breaks, not the browser's English message (invariant 13)
    return { parse: jsonFault(text), value: null, problems: [] };
  }
  if (!isSchema(value))
    return {
      parse: null,
      value: null,
      problems: [{ path: '', text: t.problem.type(typeWords(['object'])), source: 'schema' }],
    };
  return {
    parse: null,
    value,
    problems: schema ? checkSchema(value, schema).map(schemaProblem) : [],
  };
}

/** The pack with the item at `index` of `collection` replaced by `value` (added at the end for a new one, null removes it) */
export function withItem(
  pack: Pack,
  collection: Collection,
  index: number | null,
  value: Json | null,
): Pack {
  const list = [...pack[collection]];
  if (index === null) {
    if (value) list.push(value);
  } else if (value) list[index] = value;
  else list.splice(index, 1);
  return { ...pack, [collection]: list };
}

/** The server's path prefix of an item: «objects[3]», «wheels[0]» */
export const prefixOf = (collection: Collection, index: number) => `${collection}[${index}]`;

const idOf = (item: Json) => (typeof item.id === 'string' ? item.id : '');

/** Whether two definitions say the same: the order of the fields and fields left null do not count */
export const sameContent = (a: unknown, b: unknown) => stable(a) === stable(b);

/** JSON text with the keys of every object sorted: the same content written in another order reads the same */
function stable(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stable).join(',')}]`;
  if (isSchema(value))
    return `{${Object.keys(value)
      .sort()
      .filter((key) => (value as Record<string, unknown>)[key] !== null)
      .map((key) => `${JSON.stringify(key)}:${stable((value as Record<string, unknown>)[key])}`)
      .join(',')}}`;
  return JSON.stringify(value);
}

/** How a new pack differs from the one in force: ids added, changed and removed (objects and wheels together) */
export function packDiff(
  before: Pack,
  after: Pack,
): { added: number; changed: number; removed: number } {
  const map = (pack: Pack) =>
    new Map([
      ...pack.objects.map((x) => [`o:${idOf(x)}`, stable(x)] as const),
      ...pack.wheels.map((x) => [`w:${idOf(x)}`, stable(x)] as const),
    ]);
  const was = map(before);
  const now = map(after);
  let added = 0;
  let changed = 0;
  for (const [id, json] of now) {
    const old = was.get(id);
    if (old === undefined) added++;
    else if (old !== json) changed++;
  }
  const removed = [...was.keys()].filter((id) => !now.has(id)).length;
  return { added, changed, removed };
}

/** Saves `value` as a JSON file in the admin's downloads */
export function download(fileName: string, value: unknown) {
  const blob = new Blob([`${JSON.stringify(value, null, 2)}\n`], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

/** The largest pack file the server takes (AdminContentEndpoints.MaxContentRequestBytes) */
export const maxPackBytes = 1024 * 1024;
