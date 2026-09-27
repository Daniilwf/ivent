import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
import { RulesSection } from './RulesSection';
import { fieldName, valueText } from '../rules/rulesText';

// H8: the rules as JSON (SPEC «Конфиг правил сезона»; C2, C3, D-113, D-181): checked by the schema while typing;
// saved with the version it was edited from; the server's warnings shown after the save (finish.bonusesKept →
// «Уже выданные бонусы за финиш не изменятся»); «Пересчитать бонусы по текущим правилам» after a confirmation.

const t = ru.admin.rules;

const ruleset = { season: { timezone: 'Europe/Moscow', maxUncheckedRuns: 2 } };
const schema = {
  type: 'object',
  properties: {
    season: {
      type: 'object',
      properties: { timezone: { type: 'string' }, maxUncheckedRuns: { type: ['integer', 'null'] } },
      required: ['timezone'],
      additionalProperties: false,
    },
  },
  required: ['season'],
  additionalProperties: false,
};

const rules = (version: number, value: unknown = ruleset) => ({
  version,
  ruleset: value,
  history: [
    { version: 1, at: '2026-09-01T09:00:00Z', authorId: null, changes: [] },
    ...(version > 1
      ? [
          {
            version: 2,
            at: '2026-09-10T09:00:00Z',
            authorId: 'a1',
            changes: [{ path: 'season.maxUncheckedRuns', before: '2', after: '3' }],
          },
        ]
      : []),
  ],
});

function open(routes: Record<string, unknown> = {}) {
  const server = fakeServer({
    'GET /api/seasons/*/rules': rules(1),
    'GET /api/admin/rules/schema': schema,
    ...routes,
  });
  render(<RulesSection seasonId={seasonId} version={0} />);
  return server;
}

const edit = async (value: unknown) => {
  const editor = await screen.findByTestId('rules-editor');
  fireEvent.change(editor, {
    target: { value: typeof value === 'string' ? value : JSON.stringify(value, null, 2) },
  });
  return editor;
};

describe('The rules editor', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('opens the rules in force as JSON with their version; nothing to save yet', async () => {
    open();

    const editor = await screen.findByTestId('rules-editor');
    expect(JSON.parse((editor as HTMLTextAreaElement).value)).toEqual(ruleset);
    expect(screen.getByTestId('rules-version')).toHaveTextContent(ru.rules.history.version(1));
    expect(screen.getByTestId('rules-save')).toBeDisabled();
  });

  it('says a typo in JSON at once and does not send it', async () => {
    const server = open();

    await edit('{ "season": ');
    expect(await screen.findByTestId('rules-parse')).toHaveTextContent('Это не JSON');
    await userEvent.click(screen.getByTestId('rules-save'));

    expect(server.sent('PUT', '/rules')).toHaveLength(0);
  });

  it('checks the fields by the schema: unknown, missing and of the wrong type', async () => {
    const server = open();

    await edit({ season: { maxUncheckedRuns: 'два', timzone: 'x' } });

    const problems = await screen.findByTestId('rules-problems');
    expect(problems).toHaveTextContent(t.required('season.timezone'));
    expect(problems).toHaveTextContent(t.unknownField('season.timzone'));
    expect(problems).toHaveTextContent(
      t.wrongType(
        'season.maxUncheckedRuns',
        `${t.types.integer ?? ''}${t.or}${t.types.null ?? ''}`,
      ),
    );
    await userEvent.click(screen.getByTestId('rules-save'));
    expect(server.sent('PUT', '/rules')).toHaveLength(0);
  });

  it('saves the edited rules with the version they were edited from and shows the warning', async () => {
    let version = 1;
    const server = open({
      'GET /api/seasons/*/rules': () => ({
        body: rules(
          version,
          version > 1 ? { season: { ...ruleset.season, maxUncheckedRuns: 3 } } : ruleset,
        ),
      }),
      'PUT /api/admin/seasons/*/rules': () => {
        version = 2;
        return { body: { version: 2, warnings: ['finish.bonusesKept'] } };
      },
    });

    await edit({ season: { ...ruleset.season, maxUncheckedRuns: 3 } });
    await userEvent.click(screen.getByTestId('rules-save'));

    await waitFor(() => {
      expect(server.sent('PUT', '/rules')[0]?.body).toMatchObject({
        expectedVersion: 1,
        ruleset: { season: { maxUncheckedRuns: 3 } },
      });
    });
    const outcome = await screen.findByTestId('rules-outcome');
    expect(outcome).toHaveTextContent(t.saved(2));
    expect(outcome).toHaveTextContent('Уже выданные бонусы за финиш не изменятся.');
    expect(await screen.findByTestId('rules-version')).toHaveTextContent(
      ru.rules.history.version(2),
    );
    // The history reads as on the rules page: the field's name and «было/стало» in words
    const history = screen.getByTestId('rules-history');
    expect(history).toHaveTextContent(fieldName('season.maxUncheckedRuns'));
    expect(history).toHaveTextContent(`${ru.rules.history.was}: ${valueText('2')}`);
    expect(history).toHaveTextContent(`${ru.rules.history.now}: ${valueText('3')}`);
  });

  it('lists the server’s problems with their paths', async () => {
    open({
      'PUT /api/admin/seasons/*/rules': answer(400, {
        title: 'Invalid',
        status: 400,
        errors: [{ path: 'season.maxUncheckedRuns', message: 'must be at least 1' }],
      }),
    });

    await edit({ season: { ...ruleset.season, maxUncheckedRuns: 0 } });
    await userEvent.click(screen.getByTestId('rules-save'));

    expect(await screen.findByTestId('rules-server')).toHaveTextContent(
      'season.maxUncheckedRuns: must be at least 1',
    );
  });

  it('says when someone else changed the rules meanwhile', async () => {
    open({
      'PUT /api/admin/seasons/*/rules': answer(409, {
        title: 'Rejected',
        status: 409,
        code: 'ruleset.versionConflict',
      }),
    });

    await edit({ season: { ...ruleset.season, maxUncheckedRuns: 3 } });
    await userEvent.click(screen.getByTestId('rules-save'));

    expect(await screen.findByText(ru.rejection['ruleset.versionConflict'])).toBeInTheDocument();
  });

  it('puts the text back as it was', async () => {
    open();

    const editor = await edit('{}');
    await userEvent.click(screen.getByTestId('rules-reset'));

    expect(JSON.parse((editor as HTMLTextAreaElement).value)).toEqual(ruleset);
  });

  it('still edits when the schema does not load: the server checks on save', async () => {
    const server = open({
      'GET /api/admin/rules/schema': answer(500),
      'PUT /api/admin/seasons/*/rules': { version: 2, warnings: [] },
    });

    expect(await screen.findByText(t.schemaFailed)).toBeInTheDocument();
    await edit({ season: { anything: true } });
    expect(screen.queryByTestId('rules-problems')).toBeNull();
    await userEvent.click(screen.getByTestId('rules-save'));

    await waitFor(() => {
      expect(server.sent('PUT', '/rules')).toHaveLength(1);
    });
  });

  it('recalculates the finish bonuses only after a confirmation with the consequences', async () => {
    const server = open({
      'POST /api/admin/seasons/*/finish-bonuses/recalculate': { duplicate: false, events: [] },
    });

    await userEvent.click(await screen.findByTestId('recalculate'));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(t.recalcTitle)).toBeInTheDocument();
    for (const line of t.recalcConsequences)
      expect(within(dialog).getByText(line)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.recalcConfirm }));

    expect(await screen.findByText(t.recalculated)).toBeInTheDocument();
    expect(server.sent('POST', '/finish-bonuses/recalculate')).toHaveLength(1);
  });

  it('says calmly when there is nothing to recalculate', async () => {
    open({
      'POST /api/admin/seasons/*/finish-bonuses/recalculate': answer(409, {
        title: 'Rejected',
        status: 409,
        code: 'finish.nothingToRecalculate',
      }),
    });

    await userEvent.click(await screen.findByTestId('recalculate'));
    await userEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: t.recalcConfirm }),
    );

    expect(
      await screen.findByText(ru.rejection['finish.nothingToRecalculate']),
    ).toBeInTheDocument();
  });

  it('shows the history of the rules, newest first', async () => {
    open({ 'GET /api/seasons/*/rules': rules(2) });

    const history = await screen.findByTestId('rules-history');
    const entries = within(history)
      .getAllByRole('listitem')
      .filter((li) => li.parentElement?.tagName === 'OL');
    expect(entries[0]).toHaveTextContent(ru.rules.history.version(2));
    expect(entries[1]).toHaveTextContent(ru.rules.history.created);
  });
});
