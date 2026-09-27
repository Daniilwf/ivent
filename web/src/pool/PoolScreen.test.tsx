import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { demoCategories, demoGame, demoPoolGames, demoStatuses } from './demoPool';
import { PoolScreen } from './PoolScreen';
import { pageSize } from './usePaging';
import { json } from '../test/fakeServer';

// H6: the pool page — covers, search and filters, the game's status in the season, adding a game with a warning
// about alike titles (SPEC «Пул игр», «Статусы игры в сезоне», «Дубли»).

let poolChange: (() => void) | null = null;
let seasonChange: (() => void) | null = null;
// The hubs' first answer is the join: the page has just loaded and skips it (D-202)
vi.mock('../api/realtime', () => ({
  watchPool: (onChange: () => void) => {
    poolChange = onChange;
    onChange();
    return () => {
      poolChange = null;
    };
  },
  watchSeason: (_seasonId: string, onChange: (updates: unknown[]) => void) => {
    seasonChange = () => {
      onChange([{ seasonId, fromSequence: 1, toSequence: 1, types: ['game-rolled'] }]);
    };
    onChange([]);
    return () => {
      seasonChange = null;
    };
  },
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const t = ru.pool;

type Handler = (request: Request) => Response | Promise<Response>;

/** Stubs fetch by «METHOD path»; records every request with its JSON body */
function serve(routes: Record<string, Handler>) {
  const seen: { key: string; url: URL; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: Request | string, init?: RequestInit) => {
      // The typed client passes a Request; the upload passes an address and a form
      const method = input instanceof Request ? input.method : (init?.method ?? 'GET');
      const url = new URL(input instanceof Request ? input.url : input);
      const key = `${method} ${url.pathname}`;
      const body: unknown =
        input instanceof Request && method === 'POST'
          ? await input
              .clone()
              .json()
              .catch(() => null)
          : null;
      seen.push({ key, url, body });
      const handler = routes[key];
      return handler
        ? handler(input instanceof Request ? input : new Request(url))
        : new Response(null, { status: 404 });
    }),
  );
  return seen;
}

const base: Record<string, Handler> = {
  'GET /api/pool': () => json(200, demoPoolGames),
  'GET /api/pool/categories': () => json(200, demoCategories),
  [`GET /api/seasons/${seasonId}/games`]: () => json(200, demoStatuses),
};

function cards() {
  return screen.queryAllByTestId('pool-game');
}

function card(title: string) {
  const found = cards().find((c) => within(c).queryByRole('heading', { name: title }));
  if (!found) throw new Error(`no card ${title}`);
  return found;
}

function renderPool(props: Partial<Parameters<typeof PoolScreen>[0]> = {}) {
  const onSignedOut = vi.fn();
  render(<PoolScreen seasonId={seasonId} canAdd onSignedOut={onSignedOut} {...props} />);
  return { onSignedOut };
}

describe('the pool page', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('shows a skeleton, then every game with its hours, year, tags and the count', async () => {
    serve(base);
    renderPool();

    expect(screen.getByTestId('pool-loading')).toHaveAttribute('aria-busy', 'true');
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });
    expect(screen.getByTestId('pool-count')).toHaveTextContent('В пуле 7 игр');
    const witcher = card('The Witcher 3: Wild Hunt');
    expect(witcher).toHaveTextContent('51,5 ч');
    expect(witcher).toHaveTextContent('2015');
    expect(within(witcher).getByText('РПГ')).toBeInTheDocument();
    expect(card('Tetris Effect')).toHaveTextContent(ru.hours.estimate(null));
    expect(card('Portal 2')).toHaveTextContent(ru.gamePage.coop);
  });

  it('shows the cover when the pool has one and the neutral stand-in when not', async () => {
    serve({
      ...base,
      'GET /api/pool': () =>
        json(200, [
          {
            ...demoPoolGames[0],
            cover: { id: 'c1', url: '/api/files/c1', thumbnailUrl: '/api/files/c1/thumbnail' },
          },
          demoPoolGames[1],
        ]),
    });
    renderPool();

    const withCover = await waitFor(() => card('Alan Wake'));
    const img = withCover.querySelector('img');
    expect(img).toHaveAttribute('src', '/api/files/c1/thumbnail');
    expect(img).toHaveAttribute('loading', 'lazy');
    expect(
      within(card('Baba Is You')).getByRole('img', { name: ru.board.noCover }),
    ).toBeInTheDocument();
  });

  it('marks each game with its status in the season (SPEC «Статусы игры в сезоне»)', async () => {
    serve(base);
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });

    expect(card('Alan Wake')).toHaveTextContent('Уже прошёл Вася, 12.10');
    expect(card('Dead Space')).toHaveTextContent('Сейчас играет Петя');
    const dropped = card('Silent Hill 2');
    expect(dropped).toHaveTextContent(t.free);
    expect(dropped).toHaveTextContent('Дропнул Маша');
    expect(card('The Witcher 3: Wild Hunt')).toHaveTextContent(t.excluded.techRerolled);
    expect(card('Baba Is You')).toHaveTextContent(t.free);
  });

  it('shows the author and the note or the completion condition', async () => {
    serve(base);
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });

    expect(card('Silent Hill 2')).toHaveTextContent('Челлендж: концовка «In Water»');
    expect(card('Silent Hill 2')).toHaveTextContent('Добавил Маша');
    expect(card('Tetris Effect')).toHaveTextContent('Пройти режим «Путешествие»');
  });

  it('searches by title and says how many were found', async () => {
    serve(base);
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });

    await userEvent.type(screen.getByLabelText(t.search), 'dead');

    expect(cards()).toHaveLength(1);
    expect(screen.getByTestId('pool-count')).toHaveTextContent('Нашлось 1 игра из 7');
  });

  it('filters by category, length and «only free», and resets', async () => {
    serve(base);
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });

    await userEvent.click(screen.getByRole('button', { name: t.filters(0) }));
    await userEvent.selectOptions(screen.getByLabelText(t.category), 'Хоррор');
    expect(cards()).toHaveLength(3);

    await userEvent.click(screen.getByRole('radio', { name: t.lengths.medium }));
    expect(cards()).toHaveLength(3);
    await userEvent.click(screen.getByRole('radio', { name: t.lengths.short }));
    expect(screen.getByText(t.nothingTitle)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('radio', { name: t.lengths.any }));
    await userEvent.click(screen.getByRole('checkbox', { name: t.freeOnly }));
    expect(cards().map((c) => within(c).getByRole('heading').textContent)).toEqual([
      'Silent Hill 2',
    ]);
    expect(screen.getByRole('button', { name: t.filters(2) })).toHaveAttribute(
      'aria-expanded',
      'true',
    );

    await userEvent.click(screen.getByRole('button', { name: t.reset }));
    expect(cards()).toHaveLength(demoPoolGames.length);
  });

  it('offers to reset the filters when nothing is found', async () => {
    serve(base);
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });

    await userEvent.type(screen.getByLabelText(t.search), 'нет такой игры');
    expect(screen.getByRole('heading', { name: t.nothingTitle })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: t.reset }));
    expect(cards()).toHaveLength(demoPoolGames.length);
    expect(screen.getByLabelText(t.search)).toHaveValue('');
  });

  it('calls to add the first game when the pool is empty', async () => {
    serve({ ...base, 'GET /api/pool': () => json(200, []) });
    renderPool();

    expect(await screen.findByRole('heading', { name: t.emptyTitle })).toBeInTheDocument();
    expect(screen.getByText(t.emptyText)).toBeInTheDocument();
  });

  it('shows the error with a retry that loads the pool', async () => {
    let fail = true;
    serve({
      ...base,
      'GET /api/pool': () => (fail ? json(500, {}) : json(200, demoPoolGames)),
    });
    renderPool();

    expect(await screen.findByRole('heading', { name: t.loadErrorTitle })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });
  });

  it('keeps the pool when only the statuses fail, and says so', async () => {
    serve({ ...base, [`GET /api/seasons/${seasonId}/games`]: () => json(500, {}) });
    renderPool();

    expect(await screen.findByText(t.statusError)).toBeInTheDocument();
    expect(cards()).toHaveLength(demoPoolGames.length);
    expect(screen.queryAllByTestId('game-status')).toHaveLength(0);
  });

  it('without a season shows the pool with no statuses and no «only free»', async () => {
    const seen = serve(base);
    renderPool({ seasonId: null });

    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });
    expect(screen.queryAllByTestId('game-status')).toHaveLength(0);
    await userEvent.click(screen.getByRole('button', { name: t.filters(0) }));
    expect(screen.queryByRole('checkbox', { name: t.freeOnly })).toBeNull();
    expect(seen.some((r) => r.key.endsWith('/games'))).toBe(false);
  });

  it('a game none of whose categories is on the wheel is not called free', async () => {
    serve({
      ...base,
      'GET /api/pool/categories': () =>
        json(
          200,
          demoCategories.map((c) => (c.name === 'РПГ' ? { ...c, weight: 0 } : c)),
        ),
      [`GET /api/seasons/${seasonId}/games`]: () => json(200, []),
    });
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });

    expect(card('The Witcher 3: Wild Hunt')).toHaveTextContent(t.offWheel);
    await userEvent.click(screen.getByRole('button', { name: t.filters(0) }));
    await userEvent.click(screen.getByRole('checkbox', { name: t.freeOnly }));
    expect(cards().map((c) => within(c).getByRole('heading').textContent)).not.toContain(
      'The Witcher 3: Wild Hunt',
    );
  });

  it('«only free» waits for the statuses instead of passing every game', async () => {
    serve({
      ...base,
      [`GET /api/seasons/${seasonId}/games`]: () => new Promise<Response>(() => undefined),
    });
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });

    await userEvent.click(screen.getByRole('button', { name: t.filters(0) }));
    expect(screen.getByRole('checkbox', { name: t.freeOnly })).toBeDisabled();
  });

  it('shows the latest statuses when an older answer comes late', async () => {
    const answers: ((r: Response) => void)[] = [];
    serve({
      ...base,
      [`GET /api/seasons/${seasonId}/games`]: () =>
        new Promise<Response>((resolve) => answers.push(resolve)),
    });
    renderPool();
    await waitFor(() => {
      expect(answers).toHaveLength(1);
    });
    act(() => {
      seasonChange?.();
    });
    await waitFor(() => {
      expect(answers).toHaveLength(2);
    });

    await act(async () => {
      answers[1]?.(json(200, demoStatuses));
      await new Promise((resolve) => setTimeout(resolve, 20));
    });
    await waitFor(() => {
      expect(card('Alan Wake')).toHaveTextContent('Уже прошёл Вася');
    });
    await act(async () => {
      answers[0]?.(json(200, []));
      await new Promise((resolve) => setTimeout(resolve, 20));
    });
    expect(card('Alan Wake')).toHaveTextContent('Уже прошёл Вася');
  });

  it('shows a big pool page by page, and a new search starts from the first page', async () => {
    const many = Array.from({ length: 130 }, (_, i) => ({
      ...demoGame,
      id: `game-${String(i).padStart(3, '0')}`,
      title: `Игра ${String(i).padStart(3, '0')}`,
    }));
    serve({ ...base, 'GET /api/pool': () => json(200, many) });
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(pageSize);
    });

    await userEvent.click(screen.getByRole('button', { name: t.more(60, 70) }));
    expect(cards()).toHaveLength(120);
    await userEvent.click(screen.getByRole('button', { name: t.more(10, 10) }));
    expect(cards()).toHaveLength(130);
    expect(screen.queryByRole('button', { name: /Показать ещё/ })).toBeNull();

    await userEvent.type(screen.getByLabelText(t.search), 'игра');
    expect(cards()).toHaveLength(pageSize);
  });

  it('a spectator sees no «add a game»', async () => {
    serve(base);
    renderPool({ canAdd: false });

    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });
    expect(screen.queryByRole('button', { name: t.add })).toBeNull();
  });

  it('follows the hub: a new game and a new status show up without a reload', async () => {
    let games = demoPoolGames.slice(0, 2);
    let statuses = [] as typeof demoStatuses;
    serve({
      ...base,
      'GET /api/pool': () => json(200, games),
      [`GET /api/seasons/${seasonId}/games`]: () => json(200, statuses),
    });
    renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(2);
    });
    expect(card('Alan Wake')).toHaveTextContent(t.free);

    games = demoPoolGames.slice(0, 3);
    act(() => {
      poolChange?.();
    });
    await waitFor(() => {
      expect(cards()).toHaveLength(3);
    });

    statuses = demoStatuses.slice(0, 1);
    act(() => {
      seasonChange?.();
    });
    await waitFor(() => {
      expect(card('Alan Wake')).toHaveTextContent('Уже прошёл Вася');
    });
  });

  it('a session that ended leads to the sign-in', async () => {
    serve({ ...base, 'GET /api/pool': () => json(401, {}) });
    const { onSignedOut } = renderPool();

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalled();
    });
  });
});

describe('adding a game', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function openForm(routes: Record<string, Handler> = {}) {
    const seen = serve({ ...base, ...routes });
    const result = renderPool();
    await waitFor(() => {
      expect(cards()).toHaveLength(demoPoolGames.length);
    });
    await userEvent.click(screen.getByRole('button', { name: t.add }));
    const dialog = await screen.findByRole('dialog', { name: t.form.title });
    return { seen, dialog, ...result };
  }

  const similarNone: Handler = () => json(200, []);

  it('asks for a title and a category before sending', async () => {
    const { seen, dialog } = await openForm({ 'GET /api/pool/similar': similarNone });

    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submit }));

    expect(within(dialog).getByText(t.form.nameRequired)).toBeInTheDocument();
    expect(within(dialog).getByText(t.form.categoriesRequired)).toBeInTheDocument();
    expect(seen.some((r) => r.key === 'POST /api/pool')).toBe(false);
  });

  it('checks hours and year before sending', async () => {
    const { dialog } = await openForm({ 'GET /api/pool/similar': similarNone });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), 'Hollow Knight');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Экшен' }));
    await userEvent.type(within(dialog).getByLabelText(t.form.hours), '12,3');
    await userEvent.type(within(dialog).getByLabelText(t.form.year), '1800');
    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submit }));

    expect(within(dialog).getByText(t.form.hoursInvalid)).toBeInTheDocument();
    expect(within(dialog).getByText(t.form.yearInvalid)).toBeInTheDocument();
  });

  it('adds a game: the card goes to the server, the dialog closes and the game is in the list', async () => {
    const added = {
      ...demoGame,
      id: '9a000000-0000-0000-0000-0000000000aa',
      title: 'Hollow Knight',
      tags: ['Экшен'],
      hours: 26.5,
      year: 2017,
      author: 'Вася',
    };
    const { seen, dialog } = await openForm({
      'GET /api/pool/similar': similarNone,
      'POST /api/pool': () => json(200, added),
    });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), '  Hollow Knight ');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Экшен' }));
    await userEvent.type(within(dialog).getByLabelText(t.form.hours), '26,5');
    await userEvent.type(within(dialog).getByLabelText(t.form.year), '2017');
    await userEvent.type(within(dialog).getByLabelText(t.form.note), 'Без урона по боссам');
    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submit }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    const post = seen.find((r) => r.key === 'POST /api/pool');
    expect(post?.body).toMatchObject({
      title: 'Hollow Knight',
      tags: ['Экшен'],
      hours: 26.5,
      year: 2017,
      note: 'Без урона по боссам',
      isCoop: false,
      coverFileId: null,
      force: false,
    });
    expect(screen.getByText(t.added('Hollow Knight'))).toBeInTheDocument();
    expect(card('Hollow Knight')).toBeInTheDocument();
  });

  it('warns about alike titles and adds only on «Всё равно добавить»', async () => {
    const { seen, dialog } = await openForm({
      'GET /api/pool/similar': (request) =>
        new URL(request.url).searchParams.get('title') === 'Dice & Fold'
          ? json(200, [{ id: 'g9', title: 'Dice Fold', same: false }])
          : json(200, []),
      'POST /api/pool': () => json(200, { ...demoGame, id: 'new', title: 'Dice & Fold' }),
    });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), 'Dice & Fold');
    const warning = await within(dialog).findByTestId('similar-games');
    expect(warning).toHaveTextContent(t.form.similarTitle);
    expect(warning).toHaveTextContent('Dice Fold');

    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Головоломка' }));
    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submitAnyway }));

    await waitFor(() => {
      expect(seen.find((r) => r.key === 'POST /api/pool')?.body).toMatchObject({ force: true });
    });
  });

  it('says while it looks for alike titles', async () => {
    const { dialog } = await openForm({
      'GET /api/pool/similar': () => new Promise<Response>(() => undefined),
    });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), 'Celeste');

    expect(within(dialog).getByTestId('similar-checking')).toHaveTextContent(t.form.checking);
  });

  it('refuses the same title before sending', async () => {
    const { seen, dialog } = await openForm({
      'GET /api/pool/similar': () => json(200, [{ id: 'g1', title: 'Alan Wake', same: true }]),
    });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), 'alan wake');

    expect(await within(dialog).findByText(t.form.same('Alan Wake'))).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: t.form.submit })).toBeDisabled();
    expect(seen.some((r) => r.key === 'POST /api/pool')).toBe(false);
  });

  it('shows the server refusal of a duplicate under the title', async () => {
    const { dialog } = await openForm({
      'GET /api/pool/similar': similarNone,
      'POST /api/pool': () => json(409, { code: 'pool.duplicate' }),
    });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), 'Outlast');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Хоррор' }));
    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submit }));

    expect(await within(dialog).findByText(t.form.same('Outlast'))).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('an alike title added meanwhile turns into the warning, the next press confirms', async () => {
    let alike: unknown[] = [];
    const { seen, dialog } = await openForm({
      'GET /api/pool/similar': () => json(200, alike),
      'POST /api/pool': () => json(409, { code: 'pool.similar' }),
    });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), 'Outlast 2');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Хоррор' }));
    alike = [{ id: 'g3', title: 'Outlast II', same: false }];
    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submit }));

    expect(await within(dialog).findByTestId('similar-games')).toHaveTextContent('Outlast II');
    expect(within(dialog).getByRole('button', { name: t.form.submitAnyway })).toBeInTheDocument();
    // A refused command's id is spent: the next try is a new command
    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submitAnyway }));
    await waitFor(() => {
      expect(seen.filter((r) => r.key === 'POST /api/pool')).toHaveLength(2);
    });
    const [first, second] = seen.filter((r) => r.key === 'POST /api/pool');
    expect((first?.body as { commandId: string }).commandId).not.toBe(
      (second?.body as { commandId: string }).commandId,
    );
  });

  it('says when adding too often', async () => {
    const { dialog } = await openForm({
      'GET /api/pool/similar': similarNone,
      'POST /api/pool': () => json(429, {}),
    });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), 'Inside');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Головоломка' }));
    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submit }));

    expect(await within(dialog).findByText(t.form.tooOften)).toBeInTheDocument();
  });

  it('uploads a cover of my own and sends its id', async () => {
    const { seen, dialog } = await openForm({
      'GET /api/pool/similar': similarNone,
      'POST /api/files': () =>
        json(200, { id: 'f1', url: '/api/files/f1', thumbnailUrl: '/api/files/f1/thumbnail' }),
      'POST /api/pool': () => json(200, { ...demoGame, id: 'new', title: 'Limbo' }),
    });

    await userEvent.type(within(dialog).getByLabelText(t.form.name), 'Limbo');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Головоломка' }));
    await userEvent.upload(
      screen.getByTestId('cover-file'),
      new File(['x'], 'cover.png', { type: 'image/png' }),
    );
    expect(
      await within(dialog).findByRole('button', { name: t.form.coverReplace }),
    ).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.form.submit }));

    await waitFor(() => {
      expect(seen.find((r) => r.key === 'POST /api/pool')?.body).toMatchObject({
        coverFileId: 'f1',
      });
    });
  });

  it('shows why a cover did not upload', async () => {
    const { dialog } = await openForm({
      'GET /api/pool/similar': similarNone,
      'POST /api/files': () => json(422, { code: 'file.typeInvalid' }),
    });

    await userEvent.upload(
      screen.getByTestId('cover-file'),
      new File(['x'], 'cover.png', { type: 'image/png' }),
    );

    expect(
      await within(dialog).findByText(ru.upload.errors['file.typeInvalid']),
    ).toBeInTheDocument();
  });
});
