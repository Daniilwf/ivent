// The admin's editors check the typed JSON against a schema generated from the server's types while the admin types:
// the rules (docs/ruleset.schema.json, GET /api/admin/rules/schema) and the content (docs/content.schema.json,
// GET /api/admin/content/schema). The schemas use a small part of JSON Schema: type, properties, required,
// additionalProperties, enum, const, pattern, items, `true` for any value and anyOf — the content's base actions, one
// variant per `type` (D-494); this checker knows exactly that part. The server checks everything again on save,
// including the rules that a schema cannot say (ranges, time zones, references between objects).

export type JsonSchema = {
  type?: string | string[];
  properties?: Record<string, JsonSchema | boolean>;
  required?: string[];
  additionalProperties?: boolean | JsonSchema;
  enum?: unknown[];
  const?: unknown;
  pattern?: string;
  items?: JsonSchema | boolean;
  anyOf?: JsonSchema[];
  description?: string;
};

export type SchemaProblem =
  | { kind: 'required' | 'unknown' | 'pattern'; path: string }
  | { kind: 'type'; path: string; expected: string[] }
  | { kind: 'enum'; path: string; values: unknown[] };

/** Whether an answer looks like a schema this checker can use */
export function isSchema(value: unknown): value is JsonSchema {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The value a variant of anyOf requires in its `type` property: the discriminator of the base actions */
export function variantType(variant: JsonSchema): unknown {
  const type = variant.properties?.type;
  return isSchema(type) ? type.const : undefined;
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
export function checkSchema(
  value: unknown,
  schema: JsonSchema | boolean,
  path = '',
): SchemaProblem[] {
  if (typeof schema === 'boolean') return [];
  const shown = path === '' ? '$' : path;
  if (schema.enum && !schema.enum.some((option) => option === value))
    return [{ kind: 'enum', path: shown, values: schema.enum }];
  if ('const' in schema && schema.const !== value)
    return [{ kind: 'enum', path: shown, values: [schema.const] }];
  if (schema.type !== undefined) {
    const types = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!types.some((type) => fits(value, type)))
      return [{ kind: 'type', path: shown, expected: types }];
  }
  if (
    schema.pattern !== undefined &&
    typeof value === 'string' &&
    !new RegExp(schema.pattern, 'u').test(value)
  )
    return [{ kind: 'pattern', path: shown }];

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

  // A variant per base action, told apart by `type`: the value is checked against its own variant only
  if (schema.anyOf) {
    const own = schema.anyOf.find((variant) => variantType(variant) === record.type);
    if (!own) {
      const types = schema.anyOf.map(variantType).filter((type) => type !== undefined);
      return [
        'type' in record
          ? { kind: 'enum', path: join(path, 'type'), values: types }
          : { kind: 'required', path: join(path, 'type') },
      ];
    }
    return [
      ...(schema.required ?? [])
        .filter((key) => !(key in record))
        .map((key) => ({ kind: 'required' as const, path: join(path, key) })),
      ...checkSchema(value, own, path),
    ];
  }

  for (const key of schema.required ?? [])
    if (!(key in record)) problems.push({ kind: 'required', path: join(path, key) });
  for (const [key, field] of Object.entries(record)) {
    const known = properties[key];
    if (known !== undefined) problems.push(...checkSchema(field, known, join(path, key)));
    else if (schema.additionalProperties === false)
      problems.push({ kind: 'unknown', path: join(path, key) });
    else if (isSchema(schema.additionalProperties))
      problems.push(...checkSchema(field, schema.additionalProperties, join(path, key)));
  }
  return problems;
}
