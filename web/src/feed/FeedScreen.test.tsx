import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { FeedScreen } from './FeedScreen';
import { fakeServer, json } from '../test/fakeServer';

// H5: the season's feed page (SPEC «Лента», D-150) — lines by day, links to players and games, «Показать ещё»,
// undone commands marked, every state, others' lines coming in without a reload.

const live = vi.hoisted(() => ({ changes: [] as (() => void)[] }));
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: unknown[]) => void) => {
    live.changes.push(() => {
      onChange([]);
    });
    // The hub's first answer is the join: the page has just loaded and skips it (D-202)
    onChange([]);
    return () => undefined;
  },
}));

type Page = Schemas['FeedView'];
type Entry = Schemas['FeedEntryView'];

const seasonId = '99999999-9999-9999-9999-999999999999';
const vasya = '11111111-1111-1111-1111-111111111111';
const hollow = '33333333-3333-3333-3333-333333333333';
const refs = {
  players: [
    {
      id: vasya,
      userId: 'aaaaaaaa-0000-0000-0000-000000000001',
      name: 'Вася',
      avatar: null,
      hasProfile: true,
      token: 0,
    },
  ],
  games: [{ id: hollow, title: 'Hollow Knight', hasPage: true }],
  runs: [],
};

function started(sequence: number, command: string, at: string, undone = false): Entry {
  return {
    sequence,
    commandId: command,
    occurredAt: at,
    type: 'run-started',
    data: { runId: 'r', playerId: vasya, gameId: hollow },
    author: 'vasya',
    undone,
  };
}

/** The feed API: the newest page, or the page before a sequence */
function serve(pages: (before: string | null) => Response | Promise<Response>) {
  const asked: (string | null)[] = [];
  fakeServer({
    [`GET /api/seasons/${seasonId}/feed`]: ({ query }: { query: URLSearchParams }) => {
      const before = query.get('before');
      asked.push(before);
      return pages(before);
    },
  });
  return asked;
}

const page = (entries: Entry[], nextBefore: number | null = null): Page => ({
  entries,
  nextBefore,
  ...refs,
});

describe('FeedScreen', () => {
  beforeEach(() => {
    live.changes.length = 0;
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    globalThis.history.replaceState(null, '', '/');
  });

  it('shows a skeleton, then the lines under their days with links to the player and the game', async () => {
    serve(() => json(200, page([started(2, 'c2', new Date().toISOString())])));

    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(screen.getByTestId('feed-loading')).toHaveAttribute('aria-busy', 'true');
    const line = await screen.findByTestId('feed-item-c2');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(ru.feed.title);
    expect(screen.getByRole('heading', { level: 2, name: ru.feed.today })).toBeInTheDocument();
    expect(line).toHaveTextContent('Вася начинает Hollow Knight');
    expect(within(line).getByRole('link', { name: 'Вася' })).toHaveAttribute(
      'href',
      '/users/aaaaaaaa-0000-0000-0000-000000000001',
    );
    expect(within(line).getByRole('link', { name: 'Hollow Knight' })).toHaveAttribute(
      'href',
      `/games/${hollow}`,
    );
    expect(screen.getByTestId('feed-start')).toHaveTextContent(ru.feed.start);
    expect(screen.queryByTestId('feed-more')).toBeNull();
  });

  it('shows the cover of a completed game beside its line (D-222)', async () => {
    const completed: Entry = {
      sequence: 3,
      commandId: 'c3',
      occurredAt: new Date().toISOString(),
      type: 'run-completed',
      data: { runId: 'r', playerId: vasya, gameId: hollow, difficulty: 'hard', hours: 5 },
      author: 'vasya',
      undone: false,
    };
    const thumbnail = '/api/files/f1/thumbnail';
    serve(() =>
      json(200, {
        ...page([completed, started(2, 'c2', new Date().toISOString())]),
        games: [
          {
            id: hollow,
            title: 'Hollow Knight',
            hasPage: true,
            cover: { id: 'f1', url: '/api/files/f1', thumbnailUrl: thumbnail },
          },
        ],
      }),
    );

    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const line = await screen.findByTestId('feed-item-c3');
    expect(line.querySelector('img')).toHaveAttribute('src', thumbnail);
    // The start of the same game is a plain line
    expect(screen.getByTestId('feed-item-c2').querySelector('img')).toBeNull();
  });

  it('opens the game page from a line without reloading', async () => {
    serve(() => json(200, page([started(2, 'c2', new Date().toISOString())])));
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByRole('link', { name: 'Hollow Knight' }));

    expect(globalThis.location.pathname).toBe(`/games/${hollow}`);
  });

  it('loads older lines with «Показать ещё» and says where the season began', async () => {
    const asked = serve((before) =>
      before === null
        ? json(200, page([started(9, 'new', '2026-09-26T10:00:00Z')], 9))
        : json(200, page([started(3, 'old', '2026-09-20T10:00:00Z')])),
    );
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('feed-more'));

    expect(await screen.findByTestId('feed-item-old')).toBeInTheDocument();
    expect(screen.getByTestId('feed-item-new')).toBeInTheDocument();
    expect(asked).toContain('9');
    expect(screen.getByTestId('feed-start')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '20 сентября' })).toBeInTheDocument();
  });

  it('keeps the lines when older ones fail to load and lets try again', async () => {
    let fail = true;
    serve((before) =>
      before === null
        ? json(200, page([started(9, 'new', '2026-09-26T10:00:00Z')], 9))
        : fail
          ? json(500)
          : json(200, page([started(3, 'old', '2026-09-20T10:00:00Z')])),
    );
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('feed-more'));
    expect(await screen.findByRole('alert')).toHaveTextContent(ru.feed.moreError);
    expect(screen.getByTestId('feed-item-new')).toBeInTheDocument();

    fail = false;
    await userEvent.click(screen.getByTestId('feed-more'));
    expect(await screen.findByTestId('feed-item-old')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('marks an undone command in words, not only by crossing it out', async () => {
    serve(() => json(200, page([started(2, 'c2', new Date().toISOString(), true)])));
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const line = await screen.findByTestId('feed-item-c2');

    expect(line).toHaveAttribute('data-undone', 'true');
    expect(line).toHaveTextContent(ru.feed.undone);
    expect(line).toHaveTextContent(ru.feed.undoneHint);
  });

  it('calls to the first roll when the feed is empty', async () => {
    serve(() => json(200, page([])));
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: ru.feed.emptyTitle })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: ru.feed.emptyAction }));
    expect(globalThis.location.pathname).toBe('/');
  });

  it('says what broke and loads again on retry', async () => {
    let fail = true;
    serve(() => (fail ? json(500) : json(200, page([started(2, 'c2', new Date().toISOString())]))));
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.feed.errorTitle);
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    expect(await screen.findByTestId('feed-item-c2')).toBeInTheDocument();
  });

  it('says there is no such season for an unknown one', async () => {
    serve(() => json(404));
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: ru.feed.noSeasonTitle })).toBeInTheDocument();
  });

  it('hands a lost session to the sign-in', async () => {
    serve(() => json(401));
    const onSignedOut = vi.fn();
    render(<FeedScreen seasonId={seasonId} onSignedOut={onSignedOut} />);

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalled();
    });
  });

  it('brings in others’ new lines on top without a reload and says how many came', async () => {
    let entries = [started(2, 'c2', new Date().toISOString())];
    serve(() => json(200, page(entries)));
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('feed-item-c2');

    entries = [started(3, 'c3', new Date().toISOString()), ...entries];
    act(() => {
      for (const change of live.changes) change();
    });

    const fresh = await screen.findByTestId('feed-item-c3');
    expect(fresh.className).toContain('animate-arrive');
    expect(screen.getByTestId('feed-item-c2').className).not.toContain('animate-arrive');
    expect(screen.getByTestId('feed-announce')).toHaveTextContent(ru.feed.fresh(1));
    const lines = screen.getAllByTestId(/^feed-item-/).map((el) => el.dataset['testid']);
    expect(lines).toEqual(['feed-item-c3', 'feed-item-c2']);
  });
});
