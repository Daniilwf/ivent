import type { JsonSchema } from '../admin/ruleSchema';
import type { Pack } from '../admin/content/contentModel';

// The styleguide's content: an item with dice written in words (a schema error at the field), an effect, and a name
// with markup that must show as text

const value = {
  type: ['integer', 'string'],
  pattern: '^(-?([1-9][0-9]?)?d[1-9][0-9]{0,2}|-?\\$[a-zA-Z][a-zA-Z0-9]{0,39})$',
};

/** The part of docs/content.schema.json the demo needs: an object, its effect, two base actions */
export const demoContentSchema: JsonSchema = {
  type: 'object',
  properties: {
    objects: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          id: { type: 'string' },
          kind: { enum: ['item', 'effect', 'specialRoll', 'event', 'achievement'] },
          name: { type: 'string' },
          description: { type: 'string' },
          rarity: { enum: ['common', 'epic', 'legendary', null] },
          price: { type: ['integer', 'null'] },
          window: { enum: ['beforeRoll', 'afterRoll', 'beforeDice', 'afterDice', 'anytime', null] },
          hostile: { type: 'boolean' },
          stackable: { type: 'boolean' },
          manual: { type: 'boolean' },
          effect: {
            type: ['object', 'null'],
            properties: {
              target: { type: ['object', 'null'] },
              trigger: { enum: ['runCompleted', 'beforeRoll', null] },
              duration: { type: ['object', 'null'] },
              actions: {
                type: 'array',
                items: {
                  type: 'object',
                  required: ['type'],
                  anyOf: [
                    {
                      properties: {
                        type: { const: 'roll' },
                        dice: value,
                        target: { enum: ['self', null] },
                      },
                      required: ['dice'],
                      additionalProperties: false,
                    },
                    {
                      properties: {
                        type: { const: 'changeResource' },
                        resource: { type: 'string' },
                        amount: value,
                        target: { enum: ['self', null] },
                      },
                      required: ['resource', 'amount'],
                      additionalProperties: false,
                    },
                  ],
                },
              },
            },
            additionalProperties: false,
          },
        },
        required: ['id', 'kind', 'name', 'description'],
        additionalProperties: false,
      },
    },
    wheels: { type: 'array' },
  },
};

export const demoContentPack: Pack = {
  objects: [
    {
      id: 'bird-thief',
      kind: 'item',
      name: 'Птичкерс',
      description: 'Укради у выбранного игрока 1d4 монеток.',
      rarity: 'common',
      price: 15,
      window: 'anytime',
      hostile: true,
      effect: {
        target: { selector: 'chosen', excludeSelf: true },
        actions: [
          { type: 'roll', dice: 'четыре' },
          { type: 'changeResource', resource: 'coins', amount: '-$roll' },
          { type: 'changeResource', resource: 'coins', amount: '$roll', target: 'self' },
        ],
      },
    },
    {
      id: 'curse',
      kind: 'effect',
      name: 'Проклятие',
      description: 'Минус очко за следующее прохождение.\nСнимается щитом.',
      effect: {
        target: { selector: 'self' },
        trigger: 'runCompleted',
        duration: { runs: 1 },
        actions: [{ type: 'changeResource', resource: 'points', amount: -1 }],
      },
    },
    {
      id: 'markup',
      kind: 'item',
      name: '<b>Жирный</b> <img src=x onerror=alert(1)>',
      description: '<script>alert(1)</script> — это текст, не разметка.',
      rarity: 'legendary',
      window: 'beforeDice',
      stackable: true,
      effect: { actions: [{ type: 'changeResource', resource: 'points', amount: 1 }] },
    },
  ],
  wheels: [],
};
