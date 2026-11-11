// Templates of the content editor (D-496): one per base action (CONTENT.md «Базовые действия»), with example
// parameters that pass the schema, and a starting definition per kind of object. A reference to another object or
// wheel takes one the pack has, so a template inserted into a real pack passes the server's check too.

export type Json = Record<string, unknown>;

/** The base actions in the order of CONTENT.md; the schema's variants are tested to be exactly these */
export const actionTypes = [
  'move',
  'changeResource',
  'roll',
  'giveObject',
  'takeObject',
  'transformObject',
  'drawEvent',
  'spinWheel',
  'modifyNextRoll',
  'modifyDice',
  'teleport',
  'requestChoice',
] as const;

export type ActionType = (typeof actionTypes)[number];

/** What the pack has to point at: its object ids and wheel ids */
export type PackIds = { objects: readonly string[]; wheels: readonly string[] };

export function actionTemplate(type: ActionType, ids: PackIds): Json {
  switch (type) {
    case 'move':
      return { type, steps: 1 };
    case 'changeResource':
      return { type, resource: 'coins', amount: 3 };
    case 'roll':
      return { type, dice: '1d6' };
    case 'giveObject':
      return { type, objectId: ids.objects[0] ?? 'orange' };
    case 'takeObject':
      return { type, mode: 'steal', filter: { kind: 'item' }, pick: 'random' };
    case 'transformObject':
      return { type, mode: 'annotate', note: 'Помечен' };
    case 'drawEvent':
      return { type, deck: 'good' };
    case 'spinWheel':
      return { type, wheel: ids.wheels[0] ?? 'lootbox' };
    case 'modifyNextRoll':
      return { type, choiceCount: 3 };
    case 'modifyDice':
      return { type, when: 'next', stage: 'add', value: 1 };
    case 'teleport':
      return { type, cell: 'nearestShortcut' };
    case 'requestChoice':
      return { type, prompt: 'Выбери категорию', options: { from: 'categories' } };
  }
}

export const newKinds = ['item', 'effect', 'specialRoll', 'wheel'] as const;

export type NewKind = (typeof newKinds)[number];

/** An id the pack does not have yet: «new-item», «new-item-2»… */
export function freeId(base: string, taken: readonly string[]): string {
  if (!taken.includes(base)) return base;
  for (let n = 2; ; n++) if (!taken.includes(`${base}-${n}`)) return `${base}-${n}`;
}

/** A starting definition of a new object (or wheel) of `kind` that passes the schema and the server's check */
export function newObject(
  kind: NewKind,
  ids: PackIds,
  text: { name: string; description: string; wheel: string },
): Json {
  const self = { selector: 'self' };
  switch (kind) {
    case 'item':
      return {
        id: freeId('new-item', ids.objects),
        kind,
        name: text.name,
        description: text.description,
        rarity: 'common',
        price: 10,
        window: 'anytime',
        effect: {
          target: self,
          actions: [{ type: 'changeResource', resource: 'coins', amount: 3 }],
        },
      };
    case 'effect':
      return {
        id: freeId('new-effect', ids.objects),
        kind,
        name: text.name,
        description: text.description,
        effect: {
          target: self,
          trigger: 'runCompleted',
          duration: { runs: 1 },
          actions: [{ type: 'changeResource', resource: 'points', amount: 2 }],
        },
      };
    case 'specialRoll':
      return {
        id: freeId('new-special-roll', ids.objects),
        kind,
        name: text.name,
        description: text.description,
        effect: {
          target: self,
          trigger: 'beforeRoll',
          duration: { uses: 1 },
          actions: [{ type: 'modifyNextRoll', choiceCount: 3 }],
        },
      };
    case 'wheel':
      return {
        id: freeId('new-wheel', ids.wheels),
        name: text.wheel,
        entries: ids.objects.slice(0, 1).map((objectId) => ({ objectId, weight: 1 })),
      };
  }
}
