// The admin's rules editor checks the typed JSON against the ruleset schema (docs/ruleset.schema.json, served by
// GET /api/admin/rules/schema) while the admin types. The schema uses a small part of JSON Schema: type, properties,
// required, additionalProperties, enum and items; this checker knows exactly that part. The server checks everything
// again on save, including the rules that a schema cannot say (ranges, time zones).

export type JsonSchema = {
  type?: string | string[];
  properties?: Record<string, JsonSchema>;
  required?: string[];
  additionalProperties?: boolean | JsonSchema;
  enum?: unknown[];
  items?: JsonSchema;
};

export type SchemaProblem =
  | { kind: 'required' | 'unknown'; path: string }
  | { kind: 'type'; path: string; expected: string[] }
  | { kind: 'enum'; path: string; values: unknown[] };

/** Whether an answer looks like a schema this checker can use */
export function isSchema(value: unknown): value is JsonSchema {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function typeOf(value: unknown): string {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value;
}

function fits(value: unknown, type: string): boolean {
  switch (type) {
    case 'integer':
      return typeof value === 'number' && Number.isInteger(value);
    case 'number':
      return typeof value === 'number';
    case 'object':
      return typeOf(value) === 'object';
    default:
      return typeOf(value) === type;
  }
}

const join = (path: string, key: string) => (path === '' ? key : `${path}.${key}`);

/** Every place where the value does not fit the schema, with the JSON path of the field */
export function checkSchema(value: unknown, schema: JsonSchema, path = ''): SchemaProblem[] {
  const shown = path === '' ? '$' : path;
  if (schema.enum && !schema.enum.some((option) => option === value))
    return [{ kind: 'enum', path: shown, values: schema.enum }];
  if (schema.type !== undefined) {
    const types = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!types.some((type) => fits(value, type)))
      return [{ kind: 'type', path: shown, expected: types }];
  }

  const problems: SchemaProblem[] = [];
  if (Array.isArray(value)) {
    if (schema.items) {
      const items = schema.items;
      value.forEach((item, i) => {
        problems.push(...checkSchema(item, items, `${path}[${i}]`));
      });
    }
    return problems;
  }
  if (typeOf(value) !== 'object') return problems;
  const properties = schema.properties ?? {};

  const record = value as Record<string, unknown>;
  for (const key of schema.required ?? [])
    if (!(key in record)) problems.push({ kind: 'required', path: join(path, key) });
  for (const [key, field] of Object.entries(record)) {
    const known = properties[key];
    if (known) problems.push(...checkSchema(field, known, join(path, key)));
    else if (schema.additionalProperties === false)
      problems.push({ kind: 'unknown', path: join(path, key) });
    else if (isSchema(schema.additionalProperties))
      problems.push(...checkSchema(field, schema.additionalProperties, join(path, key)));
  }
  return problems;
}
