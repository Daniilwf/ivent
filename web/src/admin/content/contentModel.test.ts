import { inspect, packDiff, serverProblems, serverText, withItem } from './contentModel';
import { jsonFault, locate, places } from './jsonLocate';
import { setIn } from './paths';

// EE4 (D-492, D-495): problems by the path of their field — the server's split into the open object's and the rest,
// in Russian where the dictionary knows the message; the field found in the JSON text; the pack after a save.

describe('The content model', () => {
  it('splits the server problems into the open object and the others, by the object place', () => {
    const { own, other } = serverProblems(
      [
        { path: 'objects[1].window', message: 'An item needs a window of use.' },
        { path: 'objects[1]', message: 'Something whole' },
        { path: 'objects[10].name', message: '1–100 characters.' },
        { path: 'wheels[0].entries[0].objectId', message: '«pear» is not an object of the pack.' },
      ],
      'objects[1]',
    );

    expect(own).toEqual([
      { path: 'window', text: 'Предмету нужно окно использования.', source: 'server' },
      { path: '', text: 'Сервер: Something whole', source: 'server' },
    ]);
    expect(other).toEqual([
      { path: 'objects[10].name', text: 'От 1 до 100 символов.' },
      { path: 'wheels[0].entries[0].objectId', text: 'Объекта «pear» нет в пакете.' },
    ]);
  });

  it('tells an unknown server message as it came, marked as the server’s', () => {
    expect(serverText('A brand new rule.')).toBe('Сервер: A brand new rule.');
    // The reader's messages in the server's own words (CE-SEC-2)
    expect(serverText('Field «name» is required.')).toBe('Нужно поле «name».');
    expect(serverText('Field «colour» is unknown.')).toBe('Неизвестное поле «colour».');
    expect(serverText('A list item is an object, not null.')).toBe(
      'Элемент списка — объект, а не null.',
    );
  });

  it('says what is not JSON and what is not an object', () => {
    // Where the text breaks, by line and character, never the browser's English message (invariant 13)
    expect(inspect('{', null).parse).toEqual({ offset: 1, line: 1, column: 2 });
    expect(inspect('[]', null).problems).toHaveLength(1);
    expect(inspect('{"id":"x"}', null)).toEqual({ parse: null, value: { id: 'x' }, problems: [] });
  });

  it('replaces, adds and removes an item of the pack', () => {
    const pack = { objects: [{ id: 'a' }, { id: 'b' }], wheels: [] };

    expect(withItem(pack, 'objects', 1, { id: 'c' }).objects).toEqual([{ id: 'a' }, { id: 'c' }]);
    expect(withItem(pack, 'objects', null, { id: 'c' }).objects).toHaveLength(3);
    expect(withItem(pack, 'objects', 0, null).objects).toEqual([{ id: 'b' }]);
    expect(pack.objects).toHaveLength(2);
  });

  it('counts what a new pack adds, changes and removes; the same content in another order is unchanged', () => {
    const before = {
      objects: [{ id: 'a', name: 'A', kind: 'item' }, { id: 'b' }],
      wheels: [{ id: 'w' }],
    };
    const after = {
      objects: [{ kind: 'item', name: 'A', id: 'a' }, { id: 'c' }],
      wheels: [{ id: 'w', name: 'W' }],
    };

    expect(packDiff(before, after)).toEqual({ added: 1, changed: 1, removed: 1 });
  });

  it('finds the field of a path in the JSON text, or the object that should hold it', () => {
    const text =
      '{\n  "id": "x",\n  "effect": { "actions": [ { "type": "move", "steps": "сто" } ] }\n}';

    const steps = locate(text, 'effect.actions[0].steps');
    expect(steps && text.slice(steps.start, steps.end)).toBe('"сто"');
    const missing = locate(text, 'effect.actions[0].target');
    expect(missing && text.slice(missing.start, missing.end)).toBe(
      '{ "type": "move", "steps": "сто" }',
    );
    expect(locate(text, '')).toEqual({ start: 0, end: text.length });
    expect(places('{"a": }')).toBeNull();
  });

  it('finds where a text stops being JSON: the line and the character, as JSON.parse would refuse it', () => {
    const nbsp = String.fromCharCode(0xa0);
    expect(jsonFault('{"a": 1}')).toBeNull();
    expect(jsonFault('{\n  "id": 1,\n}')).toEqual({ offset: 13, line: 3, column: 1 });
    expect(jsonFault('')).toEqual({ offset: 0, line: 1, column: 1 });
    expect(jsonFault('{"a": 01}')).toEqual({ offset: 7, line: 1, column: 8 });
    expect(jsonFault('{"a": "x\ty"}')).toEqual({ offset: 6, line: 1, column: 7 });
    expect(jsonFault(`{"a": 1}${nbsp}`)).toEqual({ offset: 8, line: 1, column: 9 });
    expect(jsonFault('[1] x')).toEqual({ offset: 4, line: 1, column: 5 });
    for (const text of [
      '{"a": 01}',
      '{"a": "x\ty"}',
      `{"a": 1}${nbsp}`,
      '[1,]',
      '{"a" 1}',
      '-',
      '1.',
    ])
      expect(() => {
        JSON.parse(text);
      }).toThrow();
  });

  it('sets and leaves out a field at a path without touching the original', () => {
    const value = {
      effect: { target: { selector: 'self' }, actions: [{ type: 'move', steps: 1 }] },
    };

    expect(setIn(value, ['effect', 'actions', 0, 'steps'], 2)).toEqual({
      effect: { target: { selector: 'self' }, actions: [{ type: 'move', steps: 2 }] },
    });
    expect(setIn(value, ['effect', 'target'], undefined)).toEqual({
      effect: { actions: [{ type: 'move', steps: 1 }] },
    });
    expect(value.effect.actions[0]?.steps).toBe(1);
  });
});
