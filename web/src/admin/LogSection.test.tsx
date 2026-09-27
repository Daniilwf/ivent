import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
import { LogSection } from './LogSection';

// H8: the season log with undo (SPEC «Лог действий с откатом», D-104): newest first; undo of a whole command after a
// confirmation with a comment; a refusal because of later commands names them; undone commands, the season's creation
// and undos themselves offer no undo.

const t = ru.admin.log;

function command(overrides: Partial<Schemas['AdminCommandView']>): Schemas['AdminCommandView'] {
  return {
    commandId: 'c1',
    commandType: 'RollGame',
    authorName: 'Вася',
    occurredAt: '2026-09-20T09:00:00+00:00',
    undone: false,
    events: ['game-rolled'],
    ...overrides,
  };
}

const log = [
  command({
    commandId: 'c3',
    commandType: 'CompleteRun',
    events: ['run-completed', 'player-moved'],
  }),
  command({ commandId: 'c2', commandType: 'StartRun', undone: true }),
  command({ commandId: 'c1' }),
  command({ commandId: 'c0', commandType: 'CreateSeason', authorName: null }),
];

function open(routes: Record<string, unknown> = {}) {
  const server = fakeServer({ 'GET /api/admin/seasons/*/commands': log, ...routes });
  render(<LogSection seasonId={seasonId} version={0} />);
  return server;
}

describe('The season log', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('names the commands by their meaning, with the author and the count of events', async () => {
    open();

    const done = await screen.findByTestId('command-c3');
    expect(done).toHaveTextContent(String(t.commands.CompleteRun));
    expect(done).toHaveTextContent('Вася');
    expect(done).toHaveTextContent(t.events(2));
    expect(screen.getByTestId('command-c0')).toHaveTextContent(ru.admin.system);
  });

  it('offers no undo for an undone command, the season’s creation or an undo', async () => {
    open();

    await screen.findByTestId('command-c3');
    expect(within(screen.getByTestId('command-c3')).getByTestId('undo')).toBeInTheDocument();
    expect(within(screen.getByTestId('command-c2')).queryByTestId('undo')).toBeNull();
    expect(within(screen.getByTestId('command-c2')).getByText(t.undone)).toBeInTheDocument();
    expect(within(screen.getByTestId('command-c0')).queryByTestId('undo')).toBeNull();
  });

  it('undoes a whole command after the confirmation and a comment', async () => {
    const server = open({ 'POST /api/admin/seasons/*/undo': { duplicate: false, events: [] } });

    await userEvent.click(within(await screen.findByTestId('command-c1')).getByTestId('undo'));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(t.undoConsequences(1)[0] ?? '')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.undoConfirm }));
    expect(within(dialog).getByText(ru.admin.commentRequired)).toBeInTheDocument();
    expect(server.sent('POST', '/undo')).toHaveLength(0);

    await userEvent.type(within(dialog).getByTestId('undo-comment'), 'Ролл по ошибке');
    await userEvent.click(within(dialog).getByRole('button', { name: t.undoConfirm }));

    await waitFor(() => {
      expect(server.sent('POST', '/undo')[0]?.body).toMatchObject({
        targetCommandId: 'c1',
        comment: 'Ролл по ошибке',
      });
    });
    expect(await screen.findByText(t.undoneOk(String(t.commands.RollGame)))).toBeInTheDocument();
    expect(screen.queryByRole('alertdialog')).toBeNull();
  });

  it('names the later commands that hold the undo back, and keeps the dialog open', async () => {
    open({
      'POST /api/admin/seasons/*/undo': answer(409, {
        title: 'Rejected',
        status: 409,
        code: 'undo.dependents',
        related: ['c3', 'zz'],
      }),
    });

    await userEvent.click(within(await screen.findByTestId('command-c1')).getByTestId('undo'));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByTestId('undo-comment'), 'Ролл по ошибке');
    await userEvent.click(within(dialog).getByRole('button', { name: t.undoConfirm }));

    const dependents = await within(dialog).findByTestId('undo-dependents');
    expect(within(dialog).getByText(ru.rejection['undo.dependents'])).toBeInTheDocument();
    const items = within(dependents).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent(String(t.commands.CompleteRun));
    expect(items[1]).toHaveTextContent(t.dependentUnknown);
  });

  it('shows more of the log on request', async () => {
    const many = Array.from({ length: 50 }, (_, i) => command({ commandId: `m${i}` }));
    const server = open({ 'GET /api/admin/seasons/*/commands': many });

    await userEvent.click(await screen.findByTestId('log-more'));

    await waitFor(() => {
      expect(server.sent('GET', '/commands').map((c) => c.query.get('limit'))).toEqual([
        '50',
        '100',
      ]);
    });
  });

  it('says the log is empty', async () => {
    open({ 'GET /api/admin/seasons/*/commands': [] });

    expect(await screen.findByText(t.emptyTitle)).toBeInTheDocument();
    expect(screen.queryByTestId('log-more')).toBeNull();
  });
});
