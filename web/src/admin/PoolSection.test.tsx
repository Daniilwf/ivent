import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { answer, fakeServer, json, seasonId } from '../test/fakeServer';
import { PoolSection } from './PoolSection';

// H8: the pool and the category wheel (SPEC «Контент: игры, теги и веса»; D-92, D-119): weights with the count of
// available games, the empty-pool signal, adding and removing a category, changing, deleting and restoring a game.

const t = ru.admin.pool;

const categories = [
  { name: 'roguelike', weight: 3, games: 12 },
  { name: 'jrpg', weight: 1, games: 4 },
];
const stats = {
  categories: [
    { category: 'roguelike', weight: 3, available: 9 },
    { category: 'jrpg', weight: 1, available: 0 },
  ],
  playersWithoutGames: [] as { id: string; name: string }[],
};

function game(overrides: Partial<Schemas['PoolGameView']>): Schemas['PoolGameView'] {
  return {
    id: 'g1',
    title: 'Hollow Knight',
    tags: ['metroidvania'],
    hours: 27,
    year: 2017,
    steamAppId: '367520',
    cover: { id: 'f1', url: '/api/files/f1', thumbnailUrl: '/api/files/f1/thumbnail' },
    note: null,
    isCoop: false,
    author: null,
    isDeleted: false,
    completionCondition: 'Титры',
    deletionReason: null,
    ...overrides,
  };
}

function open(routes: Record<string, unknown> = {}, season: string | null = seasonId) {
  const server = fakeServer({
    'GET /api/pool/categories': categories,
    'GET /api/admin/seasons/*/pool-stats': stats,
    'GET /api/pool': [game({})],
    ...routes,
  });
  render(<PoolSection seasonId={season} />);
  return server;
}

describe('The pool section', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows each category with its weight, games in the pool and available now', async () => {
    open();

    const rogue = await screen.findByTestId('category-roguelike');
    expect(within(rogue).getByTestId('category-weight')).toHaveValue('3');
    expect(rogue).toHaveTextContent(t.inPool(12));
    expect(rogue).toHaveTextContent(t.available(9));
    expect(screen.getByTestId('category-jrpg')).toHaveTextContent(t.noneAvailable);
  });

  it('without a season shows the categories without the available count', async () => {
    const server = open({}, null);

    const rogue = await screen.findByTestId('category-roguelike');
    expect(rogue).not.toHaveTextContent(t.available(9));
    expect(server.sent('GET', '/pool-stats')).toHaveLength(0);
  });

  it('warns about players whose next roll would find no game', async () => {
    open({
      'GET /api/admin/seasons/*/pool-stats': {
        ...stats,
        playersWithoutGames: [{ id: 'p1', name: 'Вася' }],
      },
    });

    expect(await screen.findByText(t.starving('Вася'))).toBeInTheDocument();
  });

  it('saves a weight and refuses one outside 1–1000 at the field', async () => {
    const server = open({ 'PUT /api/admin/pool/categories/*': categories });

    const rogue = await screen.findByTestId('category-roguelike');
    const weight = within(rogue).getByTestId('category-weight');
    await userEvent.clear(weight);
    await userEvent.type(weight, '0');
    await userEvent.click(within(rogue).getByTestId('category-save'));
    expect(within(rogue).getByText(t.weightInvalid)).toBeInTheDocument();
    expect(server.sent('PUT', '/categories/roguelike')).toHaveLength(0);

    await userEvent.clear(weight);
    await userEvent.type(weight, '5');
    await userEvent.click(within(rogue).getByTestId('category-save'));

    await waitFor(() => {
      expect(server.sent('PUT', '/categories/roguelike')[0]?.body).toMatchObject({ weight: 5 });
    });
    expect(await screen.findByText(t.weightSaved('roguelike'))).toBeInTheDocument();
  });

  it('removes a category after a confirmation that the games stay', async () => {
    const server = open({ 'POST /api/admin/pool/categories/*/remove': categories });

    await userEvent.click(
      within(await screen.findByTestId('category-jrpg')).getByTestId('category-remove'),
    );
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(t.removeConsequences[1])).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.removeConfirm }));

    await waitFor(() => {
      expect(server.sent('POST', '/categories/jrpg/remove')).toHaveLength(1);
    });
  });

  it('adds a category by its tag and weight', async () => {
    const server = open({ 'PUT /api/admin/pool/categories/*': categories });

    await userEvent.click(await screen.findByTestId('category-add'));
    expect(screen.getByText(t.tagRequired)).toBeInTheDocument();
    await userEvent.type(screen.getByTestId('category-new-name'), 'coop');
    await userEvent.click(screen.getByTestId('category-add'));

    await waitFor(() => {
      expect(server.sent('PUT', '/categories/coop')[0]?.body).toMatchObject({ weight: 1 });
    });
  });

  it('searches the games and asks the deleted ones on request', async () => {
    const server = open();

    await screen.findByTestId('game-g1');
    await userEvent.type(screen.getByTestId('games-search'), 'hollow');
    await userEvent.click(screen.getByTestId('games-deleted'));

    await waitFor(() => {
      const last = server.sent('GET', '/api/pool').at(-1);
      expect(last?.query.get('query')).toBe('hollow');
      expect(last?.query.get('deleted')).toBe('true');
    });
  });

  it('changes a game keeping what the form does not show', async () => {
    const server = open({ 'PUT /api/admin/pool/*': game({ title: 'Hollow Knight: Voidheart' }) });

    // The pool page's form in its edit mode (D-202): the game's own tag stays choosable beside the wheel's categories
    await userEvent.click(await screen.findByTestId('game-edit'));
    const form = screen.getByTestId('game-form');
    await userEvent.type(within(form).getByLabelText(ru.pool.form.name), ': Voidheart');
    expect(within(form).getByRole('checkbox', { name: 'metroidvania' })).toBeChecked();
    await userEvent.click(within(form).getByRole('checkbox', { name: 'roguelike' }));
    await userEvent.clear(within(form).getByLabelText(ru.pool.form.hours));
    await userEvent.type(within(form).getByLabelText(ru.pool.form.hours), '30,5');
    await userEvent.click(within(form).getByTestId('game-save'));

    await waitFor(() => {
      expect(server.sent('PUT', '/api/admin/pool/g1')[0]?.body).toMatchObject({
        title: 'Hollow Knight: Voidheart',
        tags: ['metroidvania', 'roguelike'],
        hours: 30.5,
        year: 2017,
        steamAppId: '367520',
        coverFileId: 'f1',
        completionCondition: 'Титры',
        force: false,
      });
    });
  });

  it('says a refused change of a game', async () => {
    open({
      'PUT /api/admin/pool/*': answer(409, {
        title: 'Rejected',
        status: 409,
        code: 'pool.duplicate',
      }),
    });

    await userEvent.click(await screen.findByTestId('game-edit'));
    await userEvent.click(screen.getByTestId('game-save'));

    expect(await screen.findByText(ru.pool.form.same('Hollow Knight'))).toBeInTheDocument();
  });

  it('checks a changed game as the pool page checks a new one', async () => {
    const server = open({ 'PUT /api/admin/pool/*': game({}) });

    await userEvent.click(await screen.findByTestId('game-edit'));
    const form = screen.getByTestId('game-form');
    await userEvent.clear(within(form).getByLabelText(ru.pool.form.hours));
    await userEvent.type(within(form).getByLabelText(ru.pool.form.hours), '0,2');
    await userEvent.click(within(form).getByTestId('game-save'));

    expect(await within(form).findByText(ru.pool.form.hoursInvalid)).toBeInTheDocument();
    expect(server.sent('PUT', '/api/admin/pool/g1')).toHaveLength(0);
  });

  it('counts the games and shows a big pool page by page, like the pool page', async () => {
    const many = Array.from({ length: 70 }, (_, i) =>
      game({ id: `g${String(i)}`, title: `Игра ${String(i).padStart(2, '0')}` }),
    );
    open({ 'GET /api/pool': many });

    expect(await screen.findByTestId('games-count')).toHaveTextContent(ru.pool.found(70, 70));
    expect(screen.getAllByTestId('game-edit')).toHaveLength(60);
    await userEvent.click(screen.getByTestId('show-more'));
    expect(screen.getAllByTestId('game-edit')).toHaveLength(70);
    expect(screen.queryByTestId('show-more')).toBeNull();
  });

  it('deletes a game after a confirmation and restores a deleted one', async () => {
    const server = open({
      'GET /api/pool': [game({}), game({ id: 'g2', title: 'Celeste', isDeleted: true })],
      'POST /api/admin/pool/*/delete': game({ isDeleted: true }),
      'POST /api/admin/pool/*/restore': game({ id: 'g2', title: 'Celeste' }),
    });

    await userEvent.click(within(await screen.findByTestId('game-g1')).getByTestId('game-delete'));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByLabelText(t.deleteReason), '  Дубль Hollow Knight ');
    await userEvent.click(within(dialog).getByRole('button', { name: t.deleteConfirm }));
    await waitFor(() => {
      expect(server.sent('POST', '/pool/g1/delete')).toHaveLength(1);
    });
    expect(server.sent('POST', '/pool/g1/delete')[0]?.body).toMatchObject({
      reason: 'Дубль Hollow Knight',
    });

    await userEvent.click(within(screen.getByTestId('game-g2')).getByTestId('game-restore'));
    await waitFor(() => {
      expect(server.sent('POST', '/pool/g2/restore')).toHaveLength(1);
    });
  });

  it('does not delete a game without a reason (D-208)', async () => {
    const server = open();

    await userEvent.click(within(await screen.findByTestId('game-g1')).getByTestId('game-delete'));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByLabelText(t.deleteReason), '   ');
    await userEvent.click(within(dialog).getByRole('button', { name: t.deleteConfirm }));

    expect(within(dialog).getByText(t.deleteReasonRequired)).toBeInTheDocument();
    expect(within(dialog).getByLabelText(t.deleteReason)).toHaveAccessibleDescription(
      expect.stringContaining(t.deleteReasonRequired) as string,
    );
    expect(server.sent('POST', '/pool/g1/delete')).toHaveLength(0);
  });

  it('keeps a refused deletion in its window', async () => {
    open({ 'POST /api/admin/pool/*/delete': () => json(409, { code: 'pool.deleted' }) });

    await userEvent.click(within(await screen.findByTestId('game-g1')).getByTestId('game-delete'));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByLabelText(t.deleteReason), 'Дубль');
    await userEvent.click(within(dialog).getByRole('button', { name: t.deleteConfirm }));

    expect(
      await within(dialog).findByText(ru.admin.rejection['pool.deleted'] ?? ''),
    ).toBeInTheDocument();
  });

  it('shows why a deleted game was taken out of the pool', async () => {
    open({
      'GET /api/pool': [
        game({ id: 'g2', title: 'Celeste', isDeleted: true, deletionReason: 'Дубль Celeste' }),
      ],
    });

    expect(await screen.findByTestId('game-deletion-reason')).toHaveTextContent(
      t.deletionReason('Дубль Celeste'),
    );
  });

  it('invites to load the pool when it is empty', async () => {
    open({ 'GET /api/pool': [] });

    expect(await screen.findByText(ru.pool.emptyTitle)).toBeInTheDocument();
  });
});
