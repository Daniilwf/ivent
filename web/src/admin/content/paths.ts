// Paths of fields inside one definition: the same text as the schema check and the server write («effect.actions[0].steps»)

import { isSchema } from '../ruleSchema';
import type { Json } from './templates';

export type Segment = string | number;

/** The editor's path of a field: «effect.actions[0].steps», as the schema check and the server name it */
export const pathText = (path: readonly Segment[]) =>
  path.reduce<string>(
    (text, part) =>
      typeof part === 'number' ? `${text}[${part}]` : text === '' ? part : `${text}.${part}`,
    '',
  );

export function getIn(value: unknown, path: readonly Segment[]): unknown {
  let at: unknown = value;
  for (const part of path) {
    if (at === null || typeof at !== 'object') return undefined;
    at = (at as Record<string | number, unknown>)[part];
  }
  return at;
}

/** The value with the field at `path` set; undefined leaves the field out (every optional field may be left out) */
export function setIn(value: unknown, path: readonly Segment[], next: unknown): Json {
  const [head, ...rest] = path;
  if (head === undefined) return next as Json;
  if (Array.isArray(value)) {
    const copy = [...(value as unknown[])];
    copy[head as number] = setIn(copy[head as number], rest, next);
    return copy as unknown as Json;
  }
  const record: Record<string, unknown> = isSchema(value) ? value : {};
  const child = rest.length === 0 ? next : setIn(record[head], rest, next);
  return child === undefined
    ? Object.fromEntries(Object.entries(record).filter(([key]) => key !== head))
    : { ...record, [head]: child };
}
