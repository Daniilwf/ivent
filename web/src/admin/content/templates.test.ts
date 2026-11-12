import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { checkSchema, isSchema } from '../ruleSchema';
import { actionVariants, contentSchemas } from './contentModel';
import { actionTemplate, actionTypes, freeId, newKinds, newObject } from './templates';

// EE3 (D-494, D-496): the editor's templates against the schema generated from the server's types
// (docs/content.schema.json is the text GET /api/admin/content/schema serves, a .NET test keeps them equal).

const root = join(__dirname, '..', '..', '..', '..');
const pack = JSON.parse(readFileSync(join(root, 'docs', 'content.schema.json'), 'utf8')) as unknown;
if (!isSchema(pack)) throw new Error('docs/content.schema.json is not a schema');
const schemas = contentSchemas(pack);
const ids = { objects: ['orange', 'banana'], wheels: ['lootbox'] };
const text = { name: 'Новый объект', description: 'Описание', wheel: 'Новый лутбокс' };

describe('The content editor templates', () => {
  it('find the object, wheel and action schemas inside the pack schema', () => {
    expect(schemas.object?.required).toEqual(['id', 'kind', 'name', 'description']);
    expect(schemas.wheel?.required).toEqual(['id', 'name', 'entries']);
    expect(schemas.action?.anyOf).toHaveLength(12);
  });

  it('have one template per base action of the schema: twelve, no more', () => {
    expect([...actionVariants(schemas.action)].sort()).toEqual([...actionTypes].sort());
  });

  it.each(actionTypes)('«%s» passes the schema of an action', (type) => {
    const template = actionTemplate(type, ids);

    expect(template.type).toBe(type);
    expect(checkSchema(template, schemas.action ?? {})).toEqual([]);
  });

  it('point at an object and a wheel the pack has', () => {
    expect(actionTemplate('giveObject', ids)).toMatchObject({ objectId: 'orange' });
    expect(actionTemplate('spinWheel', ids)).toMatchObject({ wheel: 'lootbox' });
  });

  it.each(newKinds)('a new «%s» passes the schema', (kind) => {
    const created = newObject(kind, ids, text);

    expect(checkSchema(created, (kind === 'wheel' ? schemas.wheel : schemas.object) ?? {})).toEqual(
      [],
    );
  });

  it('give a new object an id nobody has', () => {
    expect(freeId('new-item', ['new-item', 'new-item-2'])).toBe('new-item-3');
    expect(newObject('item', { objects: ['new-item'], wheels: [] }, text).id).toBe('new-item-2');
  });

  it('agree with the content files: every file of /content passes the same schema', () => {
    for (const folder of ['items', 'wheels'] as const) {
      const dir = join(root, 'content', folder);
      for (const file of readdirSync(dir).filter((f) => f.endsWith('.json'))) {
        const value = JSON.parse(readFileSync(join(dir, file), 'utf8')) as unknown;
        expect([
          file,
          checkSchema(value, (folder === 'items' ? schemas.object : schemas.wheel) ?? {}),
        ]).toEqual([file, []]);
      }
    }
  });
});
