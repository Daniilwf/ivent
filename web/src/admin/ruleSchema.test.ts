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

  it('checks the length of a text and the range of a number (S4D-10)', () => {
    const text: JsonSchema = { type: 'string', minLength: 1, maxLength: 3 };
    const price: JsonSchema = { type: ['integer', 'null'], minimum: 0, maximum: 9 };
    expect(checkSchema('ab', text)).toEqual([]);
    expect(checkSchema('', text, 'name')).toEqual([
      { kind: 'length', path: 'name', min: 1, max: 3 },
    ]);
    expect(checkSchema('abcd', text, 'name')).toEqual([
      { kind: 'length', path: 'name', min: 1, max: 3 },
    ]);
    expect(checkSchema(null, price)).toEqual([]);
    expect(checkSchema(5, price)).toEqual([]);
    expect(checkSchema(-5, price, 'price')).toEqual([
      { kind: 'range', path: 'price', min: 0, max: 9 },
    ]);
    expect(checkSchema(10, price, 'price')).toEqual([
      { kind: 'range', path: 'price', min: 0, max: 9 },
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

// EE3 (D-494): the content schema's part — a variant per base action by `type`, constants, patterns, `true`
describe('The content schema check', () => {
  const action: JsonSchema = {
    type: 'object',
    required: ['type'],
    anyOf: [
      {
        properties: {
          type: { const: 'move' },
          steps: { type: ['integer', 'string'], pattern: '^-?\\$[a-z]+$' },
        },
        required: ['steps'],
        additionalProperties: false,
      },
      {
        properties: { type: { const: 'roll' }, dice: { type: 'string' }, extra: true },
        required: ['dice'],
        additionalProperties: false,
      },
    ],
  };

  it('checks an action against its own variant only', () => {
    expect(checkSchema({ type: 'move', steps: 2 }, action)).toEqual([]);
    expect(checkSchema({ type: 'roll', dice: '1d6', extra: { any: 'thing' } }, action)).toEqual([]);
    expect(checkSchema({ type: 'roll', steps: 2 }, action)).toEqual([
      { kind: 'required', path: 'dice' },
      { kind: 'unknown', path: 'steps' },
    ]);
  });

  it('names a missing or unknown type with the types there are', () => {
    expect(checkSchema({ steps: 1 }, action)).toEqual([{ kind: 'required', path: 'type' }]);
    expect(checkSchema({ type: 'explode' }, action, 'actions[0]')).toEqual([
      { kind: 'enum', path: 'actions[0].type', values: ['move', 'roll'] },
    ]);
  });

  it('says a text that is not of the pattern', () => {
    expect(checkSchema({ type: 'move', steps: '$roll' }, action)).toEqual([]);
    expect(checkSchema({ type: 'move', steps: 'сто' }, action)).toEqual([
      { kind: 'pattern', path: 'steps' },
    ]);
  });
});
