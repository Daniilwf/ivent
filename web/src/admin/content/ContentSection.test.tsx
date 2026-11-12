import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../../api/client';
import { moscowTime } from '../../app/time';
import { ru } from '../../i18n/ru';
import { onDesktop } from '../../test/desk';
import { answer, fakeServer, seasonId } from '../../test/fakeServer';
import { ContentSection } from './ContentSection';

// EE5–EE8 (4.12, D-490…D-498): the content editor — the objects in force by kind, one opened in a form or as JSON,
// the server's errors next to their fields, templates of the base actions, a save that publishes the whole pack made
// from the version in force, removal, a pack from a file, the versions; with items off or on a phone nothing is saved.

const t = ru.admin.content;

const orange = {
  id: 'orange',
  kind: 'item',
  name: 'Апельсин',
  description: 'Сдвинься на клетку вперёд.',
  rarity: 'common',
  price: 10,
  window: 'anytime',
  effect: { target: { selector: 'self' }, actions: [{ type: 'move', steps: 1 }] },
};
const curse = {
  id: 'curse',
  kind: 'effect',
  name: 'Проклятие',
  description: 'Минус очко за прохождение.',
  effect: {
    target: { selector: 'self' },
    trigger: 'runCompleted',
    duration: { runs: 1 },
    actions: [{ type: 'changeResource', resource: 'points', amount: -1 }],
  },
};
const lootbox = { id: 'lootbox', name: 'Лутбокс', entries: [{ objectId: 'orange', weight: 1 }] };

const view = (
  overrides: Partial<Schemas['AdminContentView']> = {},
): Schemas['AdminContentView'] => ({
  itemsEnabled: true,
  status: 'active',
  version: 2,
  pack: { objects: [orange, curse], wheels: [lootbox] },
  deleted: [{ id: 'banana', kind: 'item', name: 'Банан', deletedIn: 2 }],
  history: [
    {
      version: 2,
      at: '2026-11-10T09:00:00Z',
      authorName: 'Админ',
      comment: 'Банан убран',
      objects: 2,
      wheels: 1,
      added: ['curse'],
      changed: ['orange'],
      removed: ['banana'],
    },
    {
      version: 1,
      at: '2026-11-09T09:00:00Z',
      authorName: null,
      comment: 'Первые предметы',
      objects: 2,
      wheels: 1,
      added: ['orange', 'banana', 'lootbox'],
      changed: [],
      removed: [],
    },
  ],
  ...overrides,
});

// A small schema of the same shape as docs/content.schema.json
const schema = {
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
          effect: {
            type: ['object', 'null'],
            properties: {
              target: {
                type: ['object', 'null'],
                properties: { selector: { enum: ['self', 'chosen'] } },
                required: ['selector'],
                additionalProperties: false,
              },
              trigger: { enum: ['runCompleted', 'beforeRoll', null] },
              duration: {
                type: ['object', 'null'],
                properties: { runs: { type: ['integer', 'null'] } },
              },
              actions: {
                type: 'array',
                items: {
                  type: 'object',
                  required: ['type'],
                  anyOf: [
                    {
                      properties: {
                        type: { const: 'move' },
                        steps: {
                          type: ['integer', 'string'],
                          pattern:
                            '^(-?([1-9][0-9]?)?d[1-9][0-9]{0,2}|-?\\$[a-zA-Z][a-zA-Z0-9]{0,39})$',
                        },
                        target: { enum: ['self', null] },
                      },
                      required: ['steps'],
                      additionalProperties: false,
                    },
                    {
                      properties: {
                        type: { const: 'changeResource' },
                        resource: { type: 'string' },
                        amount: { type: ['integer', 'string'] },
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
    wheels: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          id: { type: 'string' },
          name: { type: 'string' },
          entries: { type: 'array' },
        },
        required: ['id', 'name', 'entries'],
        additionalProperties: false,
      },
    },
  },
};

const published = { duplicate: false, events: [{ sequence: 40, type: 'content-published' }] };

function open(routes: Record<string, unknown> = {}, content = view()) {
  const server = fakeServer({
    'GET /api/admin/seasons/*/content': content,
    'GET /api/admin/content/schema': schema,
    ...routes,
  });
  render(<ContentSection seasonId={seasonId} version={0} />);
  return server;
}

const openOrange = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(await screen.findByTestId('content-open-orange'));
  return screen.findByTestId('content-editor');
};

beforeEach(() => {
  Element.prototype.scrollIntoView = () => undefined;
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('The content editor', () => {
  beforeEach(() => {
    onDesktop();
  });

  it('lists the objects in force by kind, the wheels and the deleted ones, with the version', async () => {
    const user = userEvent.setup();
    open();

    expect(await screen.findByText(t.version(2))).toBeInTheDocument();
    const rows = screen.getByTestId('content-rows');
    expect(within(rows).getByText('Апельсин')).toBeInTheDocument();
    expect(within(rows).queryByText('Проклятие')).not.toBeInTheDocument();

    await user.click(
      screen.getByRole('radio', { name: new RegExp(`^${t.kinds.effect ?? ''}`, 'u') }),
    );
    expect(within(screen.getByTestId('content-rows')).getByText('Проклятие')).toBeInTheDocument();
    await user.click(
      screen.getByRole('radio', { name: new RegExp(`^${t.kinds.wheels ?? ''}`, 'u') }),
    );
    expect(within(screen.getByTestId('content-rows')).getByText('Лутбокс')).toBeInTheDocument();
    await user.click(
      screen.getByRole('radio', { name: new RegExp(`^${t.kinds.deleted ?? ''}`, 'u') }),
    );
    expect(within(screen.getByTestId('content-deleted')).getByText('Банан')).toBeInTheDocument();
    expect(screen.getByText(t.deletedIn(2))).toBeInTheDocument();
  });

  it('opens an object in the form; a change shows in the players’ view at once', async () => {
    const user = userEvent.setup();
    open();
    await openOrange(user);

    const name = screen.getByTestId('content-field-name');
    expect(name).toHaveValue('Апельсин');
    await user.clear(name);
    await user.type(name, 'Мандарин');

    expect(screen.getByTestId('content-preview-name')).toHaveTextContent('Мандарин');
    expect(screen.getByTestId('content-save')).toBeEnabled();
  });

  it('shows markup in a name as text, never as a tag', async () => {
    const user = userEvent.setup();
    open(
      {},
      view({
        pack: { objects: [{ ...orange, name: '<img src=x onerror=alert(1)>' }], wheels: [] },
      }),
    );
    await openOrange(user);

    const preview = screen.getByTestId('content-preview');
    expect(within(preview).getByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    expect(document.querySelector('img[src="x"]')).toBeNull();
  });

  it('saves the whole pack with the object replaced, made from the version in force, with the comment', async () => {
    const user = userEvent.setup();
    const server = open({ 'POST /api/admin/seasons/*/content/publish': published });
    await openOrange(user);

    await user.clear(screen.getByTestId('content-field-price'));
    await user.type(screen.getByTestId('content-field-price'), '12');
    await user.type(screen.getByTestId('content-comment'), 'Дороже');
    await user.click(screen.getByTestId('content-save'));

    await waitFor(() => {
      expect(server.sent('POST', '/content/publish')).toHaveLength(1);
    });
    const body = server.sent('POST', '/content/publish')[0]?.body as {
      expectedVersion: number;
      comment: string;
      pack: { objects: { id: string; price?: number }[]; wheels: unknown[] };
    };
    expect(body.expectedVersion).toBe(2);
    expect(body.comment).toBe('Дороже');
    expect(body.pack.objects.map((o) => [o.id, o.price])).toEqual([
      ['orange', 12],
      ['curse', undefined],
    ]);
    expect(body.pack.wheels).toEqual([lootbox]);
    expect(await screen.findByText(t.saved(3))).toBeInTheDocument();
  });

  it('has nothing to save when a field is taken out and put back as it was', async () => {
    const user = userEvent.setup();
    open();
    await openOrange(user);

    await user.selectOptions(screen.getByTestId('content-field-window'), '');
    expect(screen.getByTestId('content-save')).toBeEnabled();
    await user.selectOptions(screen.getByTestId('content-field-window'), 'anytime');

    expect(screen.getByTestId('content-save')).toBeDisabled();
    expect(screen.queryByText(t.dirty)).not.toBeInTheDocument();
  });

  it('asks for a comment before saving', async () => {
    const user = userEvent.setup();
    const server = open({ 'POST /api/admin/seasons/*/content/publish': published });
    await openOrange(user);

    await user.type(screen.getByTestId('content-field-name'), '!');
    await user.click(screen.getByTestId('content-save'));

    expect(await screen.findByText(ru.admin.commentRequired)).toBeInTheDocument();
    expect(server.sent('POST', '/content/publish')).toHaveLength(0);
  });

  it('puts the server’s error next to its field, in Russian, and leads to it in the view that is open', async () => {
    const user = userEvent.setup();
    open({
      'POST /api/admin/seasons/*/content/publish': answer(400, {
        title: 'The content pack is invalid.',
        status: 400,
        errors: [{ path: 'objects[0].window', message: 'An item needs a window of use.' }],
      }),
    });
    await openOrange(user);

    await user.selectOptions(screen.getByTestId('content-field-window'), '');
    await user.type(screen.getByTestId('content-comment'), 'Без окна');
    await user.click(screen.getByTestId('content-save'));

    const field = await screen.findByTestId('content-field-window');
    await waitFor(() => {
      expect(field).toHaveAccessibleDescription(t.serverMessages['An item needs a window of use.']);
    });
    const problems = screen.getByTestId('content-problems');
    expect(within(problems).getByText('window')).toBeInTheDocument();

    // In the form the link stays in the form: the field with its error gets the focus
    await user.click(within(problems).getByRole('button', { name: `${t.goToField}: window` }));
    await waitFor(() => {
      expect(screen.getByTestId('content-field-window')).toHaveFocus();
    });
    expect(screen.getByTestId('content-form')).toBeInTheDocument();
    expect(screen.queryByTestId('content-json')).not.toBeInTheDocument();

    // In the JSON the same link selects the field in the text
    await user.click(screen.getByRole('radio', { name: 'JSON' }));
    await user.click(
      within(screen.getByTestId('content-problems')).getByRole('button', {
        name: `${t.goToField}: window`,
      }),
    );
    const json = screen.getByTestId('content-json');
    await waitFor(() => {
      expect(json).toHaveFocus();
    });
    // The object had no window any more: the selection is the whole object that should hold it
    const area = json as HTMLTextAreaElement;
    expect(area.value.slice(area.selectionStart, area.selectionEnd).startsWith('{')).toBe(true);
  });

  it('leads from the form to the JSON only for a part the form does not edit', async () => {
    const user = userEvent.setup();
    open();
    await openOrange(user);
    await user.click(screen.getByRole('radio', { name: 'JSON' }));
    fireEvent.change(screen.getByTestId('content-json'), {
      target: {
        value: JSON.stringify(
          { ...orange, effect: { ...orange.effect, condition: { always: true } } },
          null,
          2,
        ),
      },
    });
    await user.click(screen.getByRole('radio', { name: 'Форма' }));

    await user.click(
      within(screen.getByTestId('content-problems')).getByRole('button', {
        name: `${t.goToField}: effect.condition`,
      }),
    );

    const json = await screen.findByTestId('content-json');
    await waitFor(() => {
      expect(json).toHaveFocus();
    });
    const area = json as HTMLTextAreaElement;
    expect(area.value.slice(area.selectionStart, area.selectionEnd)).toBe(
      '{\n      "always": true\n    }',
    );
  });

  it('does not re-announce the whole list of errors while typing; only their count, politely', async () => {
    const user = userEvent.setup();
    open();
    const editor = await openOrange(user);

    await user.clear(screen.getByTestId('content-field-effect.actions[0].steps'));
    await user.type(screen.getByTestId('content-field-effect.actions[0].steps'), 'сто');

    expect(within(editor).getByTestId('content-problems')).toHaveTextContent(
      'effect.actions[0].steps',
    );
    // Neither the summary nor the field's own error is an alert while the admin types
    expect(within(editor).queryAllByRole('alert')).toHaveLength(0);
    expect(within(editor).getByRole('status')).toHaveTextContent(t.problemsTitle(1));
    expect(screen.getByTestId('content-field-effect.actions[0].steps')).toHaveAccessibleDescription(
      expect.stringContaining(t.problem.pattern),
    );
  });

  it('says the answer of «Проверить» at once: the count of the server’s errors', async () => {
    const user = userEvent.setup();
    open({
      'POST /api/admin/seasons/*/content/check': {
        valid: false,
        problems: [{ path: 'objects[0].window', message: 'An item needs a window of use.' }],
      },
    });
    const editor = await openOrange(user);

    await user.click(within(editor).getByTestId('content-check'));

    expect(await within(editor).findByRole('alert')).toHaveTextContent(t.checkedProblems(1));
    expect(within(editor).getByTestId('content-problems')).toHaveTextContent('window');
  });

  it('checks the JSON by the schema while typing and does not save what does not fit', async () => {
    const user = userEvent.setup();
    open();
    await openOrange(user);
    await user.click(screen.getByRole('radio', { name: 'JSON' }));

    const json = screen.getByTestId('content-json');
    fireEvent.change(json, {
      target: {
        value: JSON.stringify({ ...orange, effect: { actions: [{ type: 'move', steps: 'сто' }] } }),
      },
    });

    expect(
      within(screen.getByTestId('content-problems')).getByText('effect.actions[0].steps'),
    ).toBeInTheDocument();
    expect(screen.getByTestId('content-save')).toBeDisabled();

    fireEvent.change(json, { target: { value: '{"id":' } });
    // Where the text breaks, in Russian, never the browser's English message (invariant 13)
    const problems = screen.getByTestId('content-problems');
    expect(problems).toHaveTextContent(t.parseError(1, 7));
    expect(problems).not.toHaveTextContent(/JSON at|Unexpected|position/u);
    await user.click(within(problems).getByRole('button', { name: t.showPlace }));
    await waitFor(() => {
      expect(json).toHaveFocus();
    });
    expect((json as HTMLTextAreaElement).selectionStart).toBe(6);
    await user.click(screen.getByRole('radio', { name: 'Форма' }));
    expect(screen.getByText(t.formBroken)).toBeInTheDocument();
  });

  it('inserts a base action from its template at the end of the actions', async () => {
    const user = userEvent.setup();
    open();
    await openOrange(user);

    await user.selectOptions(screen.getByTestId('content-template'), 'changeResource');
    await user.click(screen.getByTestId('content-insert'));

    expect(screen.getByTestId('content-action-1')).toHaveAccessibleName(
      t.action(2, t.actions.changeResource?.name ?? ''),
    );
    expect(screen.getByTestId('content-field-effect.actions[1].resource')).toHaveValue('coins');
  });

  it('takes an id another object has as an error at the id', async () => {
    const user = userEvent.setup();
    open();
    await openOrange(user);

    const id = screen.getByTestId('content-field-id');
    await user.clear(id);
    await user.type(id, 'curse');

    expect(id).toHaveAccessibleDescription(new RegExp(t.idTaken('curse'), 'u'));
    expect(screen.getByTestId('content-save')).toBeDisabled();
  });

  it('removes an object by publishing the pack without it, after a confirmation', async () => {
    const user = userEvent.setup();
    const server = open({ 'POST /api/admin/seasons/*/content/publish': published });
    await openOrange(user);

    await user.type(screen.getByTestId('content-comment'), 'Апельсин больше не нужен');
    await user.click(screen.getByTestId('content-remove'));
    await user.click(await screen.findByTestId('content-remove-confirm-yes'));

    await waitFor(() => {
      expect(server.sent('POST', '/content/publish')).toHaveLength(1);
    });
    const body = server.sent('POST', '/content/publish')[0]?.body as {
      pack: { objects: { id: string }[] };
    };
    expect(body.pack.objects.map((o) => o.id)).toEqual(['curse']);
    expect(await screen.findByText(t.removed('Апельсин', 3))).toBeInTheDocument();
  });

  it('says that the content changed meanwhile when the server refuses an old version', async () => {
    const user = userEvent.setup();
    open({
      'POST /api/admin/seasons/*/content/publish': answer(409, { code: 'content.versionChanged' }),
    });
    await openOrange(user);

    await user.type(screen.getByTestId('content-field-name'), '!');
    await user.type(screen.getByTestId('content-comment'), 'Правка');
    await user.click(screen.getByTestId('content-save'));

    expect(
      await screen.findByText(ru.admin.rejection['content.versionChanged'] ?? ''),
    ).toBeInTheDocument();
  });

  it('creates a new object from its template and adds it to the pack', async () => {
    const user = userEvent.setup();
    const server = open({ 'POST /api/admin/seasons/*/content/publish': published });
    await screen.findByTestId('content-rows');

    await user.selectOptions(screen.getByTestId('content-new-kind'), 'item');
    await user.click(screen.getByTestId('content-new'));
    expect(screen.getByTestId('content-field-kind')).toHaveValue('item');
    await user.type(screen.getByTestId('content-comment'), 'Новый предмет');
    await user.click(screen.getByTestId('content-save'));

    await waitFor(() => {
      expect(server.sent('POST', '/content/publish')).toHaveLength(1);
    });
    const body = server.sent('POST', '/content/publish')[0]?.body as {
      pack: { objects: { id: string }[] };
    };
    expect(body.pack.objects.map((o) => o.id)).toEqual(['orange', 'curse', 'new-item']);
  });

  it('checks a pack from a file on the server and publishes it whole after a confirmation', async () => {
    const user = userEvent.setup();
    const server = open({
      'POST /api/admin/seasons/*/content/check': { valid: true, problems: [] },
      'POST /api/admin/seasons/*/content/publish': published,
    });
    const file = new File([JSON.stringify({ objects: [orange], wheels: [] })], 'pack.json', {
      type: 'application/json',
    });

    await user.upload(await screen.findByTestId('content-import-file'), file);

    expect(await screen.findByText(t.importValid)).toBeInTheDocument();
    expect(screen.getByText(new RegExp(t.importDiff(0, 0, 2), 'u'))).toBeInTheDocument();
    await user.type(screen.getByTestId('content-import-comment'), 'Из файла');
    await user.click(screen.getByTestId('content-import-publish'));
    await user.click(await screen.findByTestId('content-import-confirm-yes'));

    await waitFor(() => {
      expect(server.sent('POST', '/content/publish')).toHaveLength(1);
    });
    expect(server.sent('POST', '/content/publish')[0]?.body).toMatchObject({
      expectedVersion: 2,
      comment: 'Из файла',
      pack: { objects: [orange], wheels: [] },
    });
  });

  it('shows the problems of a file with their paths and does not let it be published', async () => {
    const user = userEvent.setup();
    open({
      'POST /api/admin/seasons/*/content/check': {
        valid: false,
        problems: [{ path: 'objects[0].window', message: 'An item needs a window of use.' }],
      },
    });
    const file = new File([JSON.stringify({ objects: [{ ...orange, window: null }] })], 'bad.json');

    await user.upload(await screen.findByTestId('content-import-file'), file);

    const problems = await screen.findByTestId('content-import-problems');
    expect(problems).toHaveTextContent('objects[0].window');
    expect(problems).toHaveTextContent(t.serverMessages['An item needs a window of use.'] ?? '');
    expect(screen.getByTestId('content-import-publish')).toBeDisabled();
  });

  it('says where a file stops being JSON, in Russian, before asking the server', async () => {
    const user = userEvent.setup();
    const server = open();

    await user.upload(
      await screen.findByTestId('content-import-file'),
      new File(['{"objects": [}'], 'broken.json'),
    );

    expect(await screen.findByText(t.importNotJson(1, 14))).toBeInTheDocument();
    expect(screen.getByTestId('content-import')).not.toHaveTextContent(
      /JSON at|Unexpected|position/u,
    );
    expect(server.sent('POST', '/content/check')).toHaveLength(0);
  });

  it('names the import panel by what it holds and the file button by what it does', async () => {
    open();

    const panel = await screen.findByTestId('content-import');
    expect(within(panel).getByRole('heading', { name: t.importPanel })).toBeInTheDocument();
    expect(within(panel).getByLabelText(new RegExp(t.importPick, 'u'))).toBeInTheDocument();
    expect(t.importPanel).not.toBe(t.importPick);
  });

  it('says once that an empty season has no content and offers to create or load it', async () => {
    const user = userEvent.setup();
    open({}, view({ version: 0, pack: { objects: [], wheels: [] }, deleted: [], history: [] }));

    const list = await screen.findByTestId('content-list');
    expect(within(list).getByRole('heading', { name: t.emptyTitle })).toBeInTheDocument();
    expect(screen.getAllByText(t.emptyTitle)).toHaveLength(1);
    expect(screen.queryByText(t.emptyKind)).not.toBeInTheDocument();
    expect(screen.queryByTestId('content-filter')).not.toBeInTheDocument();
    expect(within(list).getByTestId('content-new')).toBeInTheDocument();

    const click = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => undefined);
    await user.click(within(list).getByRole('button', { name: t.loadPack }));
    expect(click).toHaveBeenCalled();
    click.mockRestore();
  });

  it('marks a stackable object for players with the glossary term, the same as the form’s label', async () => {
    const user = userEvent.setup();
    open(
      {},
      view({ pack: { objects: [{ ...orange, stackable: true }, curse], wheels: [lootbox] } }),
    );
    await openOrange(user);

    expect(
      within(screen.getByTestId('content-preview')).getByText(t.stackable),
    ).toBeInTheDocument();
    expect(screen.getByLabelText(t.fields.stackable ?? '')).toBeChecked();
    expect(t.fields.stackable).toBe(t.stackable);
    expect(t.stackable).toBe('Можно несколько сразу');
  });

  it('shows an interception of several kinds as it is and names a bad event in Russian', async () => {
    // The Бабл (5.3, D-555): `intercept` is a list; the form's single choice would show it empty
    const bubble = {
      id: 'bubble-effect',
      kind: 'effect',
      name: 'Бабл',
      description: 'Гасит следующий плохой ивент или враждебный эффект.',
      effect: {
        trigger: 'hostileIncoming',
        intercept: ['badEvent', 'hostile'],
        duration: { uses: 1 },
        actions: [],
      },
    };
    const user = userEvent.setup();
    open({}, view({ pack: { objects: [orange, curse, bubble], wheels: [lootbox] } }));
    await user.click(
      await screen.findByRole('radio', { name: new RegExp(`^${t.kinds.effect ?? ''}`, 'u') }),
    );
    await user.click(await screen.findByTestId('content-open-bubble-effect'));
    await screen.findByTestId('content-editor');

    expect(screen.queryByTestId('content-field-effect.intercept')).toBeNull();
    expect(screen.getByText('["badEvent","hostile"]')).toBeInTheDocument();
    expect(t.values.intercept?.badEvent).toBe('плохой ивент');
  });

  it('refuses a file that is not a pack before asking the server', async () => {
    const user = userEvent.setup();
    const server = open();

    await user.upload(
      await screen.findByTestId('content-import-file'),
      new File(['[1, 2]'], 'list.json'),
    );

    expect(await screen.findByText(t.importNotPack)).toBeInTheDocument();
    expect(server.sent('POST', '/content/check')).toHaveLength(0);
  });

  it('lists the versions with what they changed and downloads an older one', async () => {
    const user = userEvent.setup();
    const createObjectURL = vi.fn(() => 'blob:content');
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() }));
    const server = open({
      'GET /api/admin/seasons/*/content/versions/*': {
        version: 1,
        pack: { objects: [], wheels: [] },
      },
    });

    const history = await screen.findByTestId('content-history');
    expect(history).toHaveTextContent(t.historyRow(2, moscowTime('2026-11-10T09:00:00Z'), 'Админ'));
    expect(history).toHaveTextContent(t.historyChanges(1, 1, 1));
    expect(history).toHaveTextContent('«Первые предметы»');

    await user.click(screen.getByTestId('content-export-1'));

    await waitFor(() => {
      expect(createObjectURL).toHaveBeenCalled();
    });
    expect(server.sent('GET', '/content/versions/1')).toHaveLength(1);
  });

  it('shows the history of the open object', async () => {
    const user = userEvent.setup();
    open();
    await openOrange(user);

    const history = screen.getByTestId('content-object-history');
    expect(history).toHaveTextContent(t.objectChange.changed ?? '');
    expect(history).toHaveTextContent(t.objectChange.added ?? '');
  });

  it('with items off shows the editor and the check, but saves nothing', async () => {
    const user = userEvent.setup();
    const server = open(
      { 'POST /api/admin/seasons/*/content/check': { valid: true, problems: [] } },
      view({ itemsEnabled: false }),
    );

    expect(await screen.findByText(t.itemsOff)).toBeInTheDocument();
    await openOrange(user);
    await user.type(screen.getByTestId('content-field-name'), '!');

    expect(screen.getByTestId('content-save')).toBeDisabled();
    expect(screen.getByTestId('content-remove')).toBeDisabled();
    await user.click(screen.getByTestId('content-check'));
    expect(await screen.findByText(t.checked)).toBeInTheDocument();
    expect(server.sent('POST', '/content/check')).toHaveLength(1);
  });

  it('in a closed season says so and saves nothing', async () => {
    const user = userEvent.setup();
    open({}, view({ status: 'finished' }));

    expect(await screen.findByText(t.closed)).toBeInTheDocument();
    await openOrange(user);
    expect(screen.getByTestId('content-save')).toBeDisabled();
  });
});

describe('The content editor on a phone', () => {
  it('shows the content to look at: the JSON read-only, nothing to save or import', async () => {
    const user = userEvent.setup();
    open();

    expect(await screen.findByText(t.phone)).toBeInTheDocument();
    expect(screen.queryByTestId('content-import')).not.toBeInTheDocument();
    expect(screen.queryByTestId('content-new')).not.toBeInTheDocument();
    await openOrange(user);

    expect(screen.getByTestId('content-json')).toHaveAttribute('readonly');
    expect(screen.queryByTestId('content-save')).not.toBeInTheDocument();
    expect(screen.getByTestId('content-preview')).toBeInTheDocument();
  });
});
