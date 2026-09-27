import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { checkSchema, isSchema, type JsonSchema } from './ruleSchema';

// H8 (D-181): the rules editor's check against the ruleset schema — the part of JSON Schema the schema uses.

const schema: JsonSchema = {
  type: 'object',
  properties: {
    season: {
      type: 'object',
      properties: {
        maxUncheckedRuns: { type: ['integer', 'null'] },
        timezone: { type: 'string' },
      },
      required: ['timezone'],
      additionalProperties: false,
    },
    features: {
      type: 'object',
      properties: { mapMode: { enum: ['linear', 'graph'] }, shop: { type: 'boolean' } },
    },
    bonusByOrder: { type: 'array', items: { type: 'integer' } },
    extra: { type: 'object', additionalProperties: { type: 'number' } },
  },
  required: ['season'],
};

describe('The ruleset schema check', () => {
  it('passes a value that fits', () => {
    expect(
      checkSchema(
        {
          season: { timezone: 'Europe/Moscow', maxUncheckedRuns: null },
          features: { mapMode: 'linear', shop: false },
          bonusByOrder: [5, 3],
          extra: { a: 1.5 },
        },
        schema,
      ),
    ).toEqual([]);
  });

  it('names a missing required field and an unknown one with their paths', () => {
    expect(checkSchema({ season: { timezne: 'x' } }, schema)).toEqual([
      { kind: 'required', path: 'season.timezone' },
      { kind: 'unknown', path: 'season.timezne' },
    ]);
  });

  it('tells a whole number from a fraction, and allows null where the schema does', () => {
    expect(checkSchema({ season: { timezone: 'x', maxUncheckedRuns: 1.5 } }, schema)).toEqual([
      { kind: 'type', path: 'season.maxUncheckedRuns', expected: ['integer', 'null'] },
    ]);
    expect(checkSchema({ season: { timezone: 'x', maxUncheckedRuns: null } }, schema)).toEqual([]);
  });

  it('checks the list items and the closed lists of values', () => {
    expect(
      checkSchema(
        { season: { timezone: 'x' }, bonusByOrder: [5, '3'], features: { mapMode: 'spiral' } },
        schema,
      ),
    ).toEqual([
      { kind: 'type', path: 'bonusByOrder[1]', expected: ['integer'] },
      { kind: 'enum', path: 'features.mapMode', values: ['linear', 'graph'] },
    ]);
  });

  it('checks the values of an open dictionary by its schema', () => {
    expect(checkSchema({ season: { timezone: 'x' }, extra: { a: 'много' } }, schema)).toEqual([
      { kind: 'type', path: 'extra.a', expected: ['number'] },
    ]);
  });

  it('says a whole document of the wrong kind at its root', () => {
    expect(checkSchema([1, 2], schema)).toEqual([
      { kind: 'type', path: '$', expected: ['object'] },
    ]);
  });

  it('knows a schema-like answer', () => {
    expect(isSchema({ type: 'object' })).toBe(true);
    expect(isSchema(null)).toBe(false);
    expect(isSchema([])).toBe(false);
  });
});

describe('The ruleset schema check on the real files', () => {
  // The schema the server serves is the committed one (RulesApiTests); the default rules must fit it here too
  const read = (name: string): unknown =>
    JSON.parse(readFileSync(join(process.cwd(), '..', 'docs', name), 'utf8'));

  it('finds nothing wrong with the default rules', () => {
    const schema = read('ruleset.schema.json');
    expect(isSchema(schema)).toBe(true);
    expect(checkSchema(read('ruleset.default.json'), schema as JsonSchema)).toEqual([]);
  });

  it('finds a typo in the default rules', () => {
    const rules = read('ruleset.default.json') as { season: Record<string, unknown> };
    rules.season.maxUncheckedRun = 2;
    expect(checkSchema(rules, read('ruleset.schema.json') as JsonSchema)).toEqual([
      { kind: 'unknown', path: 'season.maxUncheckedRun' },
    ]);
  });
});
