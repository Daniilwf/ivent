import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { FeedScreen } from './FeedScreen';
import { navigate } from '../app/router';
import { fakeServer, json } from '../test/fakeServer';

// H5: the season's feed page (SPEC «Лента», D-150) — lines by day, links to players and games, «Показать ещё»,
// undone commands marked, every state, others' lines coming in without a reload.

const live = vi.hoisted(() => ({ changes: [] as ((updates?: unknown[]) => void)[] }));
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: unknown[]) => void) => {
    live.changes.push((updates = []) => {
      onChange(updates);
    });
    // The hub's first answer is the join: the page has just loaded and skips it (D-202)
    onChange([]);
    return () => undefined;
  },
}));

type Page = Schemas['FeedView'];
type Entry = Schemas['FeedEntryView'];

const seasonId = '99999999-9999-9999-9999-999999999999';
const feedPath = `/api/seasons/${seasonId}/feed`;
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

function undoSummary(target: string): Entry {
  return {
    sequence: 10,
    commandId: '1c000000-0000-0000-0000-000000000012',
    occurredAt: '2026-09-26T10:00:00Z',
    type: 'command-undone',
    data: { commandId: target, comment: 'Undo test' },
    author: 'admin',
    undone: false,
  };
}

/** The feed API: the newest page, or the page before a sequence */
function serve(pages: (before: string | null) => Response | Promise<Response>) {
  const asked: (string | null)[] = [];
  fakeServer({
    [`GET ${feedPath}`]: ({ query }: { query: URLSearchParams }) => {
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

const fire = (count: number, selected = false) => ({ emoji: '🔥', count, selected });
const groupName = 'Реакции на запись';
const fireName = 'Реакция 🔥';

function notify(sequence: number, type: string, commentTarget?: { kind: string; id: string }) {
  act(() => {
    for (const change of live.changes) {
      change([
        { seasonId, fromSequence: sequence, toSequence: sequence, types: [type], commentTarget },
      ]);
    }
  });
}

async function flushLiveChange(
  sequence: number,
  type: string,
  commentTarget?: { kind: string; id: string },
) {
  vi.useFakeTimers();
  try {
    notify(sequence, type, commentTarget);
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });
  } finally {
    vi.useRealTimers();
  }
}

describe('FeedScreen', () => {
  beforeEach(() => {
    live.changes.length = 0;
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    globalThis.history.replaceState(null, '', '/');
  });

  it('updates a loaded older placeholder when its reveal receipt arrives', async () => {
    let disclosed = false;
    const hidden = {
      ...started(1, 'older-roll', '2026-09-26T09:00:00Z'),
      type: 'game-roll-hidden',
      data: { playerId: vasya, revealId: 'older-reveal' },
    };
    const revealed = {
      ...hidden,
      type: 'game-rolled',
      data: { playerId: vasya, gameId: hollow, category: 'Adventure' },
    };
    const newest = started(3, 'newest', '2026-09-26T10:00:00Z');
    const receipt = {
      ...started(4, 'shown', '2026-09-26T10:01:00Z'),
      type: 'roll-game-shown',
      data: { playerId: vasya, revealId: 'older-reveal' },
    };
    const queried = serve((before) => {
      if (before === null) return json(200, page(disclosed ? [receipt, newest] : [newest], 3));
      if (before === '3') return json(200, page([hidden]));
      if (before === '2') return json(200, page([revealed]));
      return json(404, {});
    });
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('feed-more'));
    const roll = await screen.findByTestId('feed-item-older-roll');
    expect(roll).toHaveTextContent('Вася крутит колесо игр…');
    expect(roll).not.toHaveTextContent('Hollow Knight');
    disclosed = true;
    await flushLiveChange(4, 'roll-game-shown');

    await waitFor(() => expect(roll).toHaveTextContent('Вася выкручивает Hollow Knight'));
    expect(screen.queryByTestId('feed-item-shown')).not.toBeInTheDocument();
    expect(queried).toEqual([null, '3', null, '2']);
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

  it('keeps one accessible reaction group when a command spans two feed pages', async () => {
    const at = '2026-09-26T10:00:00Z';
    const command = '1c000000-0000-0000-0000-000000000001';
    const completion = {
      ...started(8, command, at),
      type: 'run-completed',
      data: { runId: 'r', playerId: vasya, gameId: hollow, difficulty: 'hard', hours: 5 },
    };
    const movement = {
      ...started(9, command, at),
      type: 'player-moved',
      data: { playerId: vasya },
    };
    const reactions = { [command]: [fire(1)] };
    serve((before) =>
      json(200, {
        ...(before === null ? page([movement], 9) : page([completion])),
        reactions,
      }),
    );
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await user.click(await screen.findByTestId('feed-more'));
    await screen.findByTestId('feed-start');

    expect(screen.getAllByTestId(`feed-item-${command}`)).toHaveLength(1);
    const row = screen.getByTestId(`feed-item-${command}`);
    const group = within(row).getByRole('group', { name: groupName });
    expect(screen.getAllByRole('group', { name: groupName })).toHaveLength(1);
    expect(
      within(group).getByRole('button', { name: fireName, pressed: false }),
    ).toHaveAccessibleDescription('1 реакция');
  });

  it('opens the selected feed comment thread and sends a plain-text comment to that row', async () => {
    const at = '2026-09-26T10:00:00Z';
    const eligible = '1c000000-0000-0000-0000-000000000081';
    const unavailable = '1c000000-0000-0000-0000-000000000082';
    const threadPath = `${feedPath}/${eligible}/comments`;
    const otherText = 'Показываем <b>текст</b> буквально.';
    const ownText = 'Спасибо за прохождение!';
    const otherComment = {
      id: '1c000000-0000-0000-0000-000000000083',
      author: 'Петя',
      text: otherText,
      createdAt: at,
      editedAt: null,
      canEdit: false,
      canHide: false,
    };
    let submitted: { commentId: string; text: string } | null = null;
    const server = fakeServer({
      [`GET ${feedPath}`]: {
        ...page([started(9, eligible, at), started(8, unavailable, at, true)]),
        commentTargets: [eligible],
      },
      [`GET ${threadPath}`]: () =>
        json(200, {
          comments: submitted
            ? [
                otherComment,
                {
                  ...otherComment,
                  id: submitted.commentId,
                  author: 'Вася',
                  text: submitted.text,
                  canEdit: true,
                },
              ]
            : [otherComment],
          nextBefore: null,
        }),
      [`POST ${threadPath}`]: ({ body }) => {
        submitted = body as { commentId: string; text: string };
        return json(204);
      },
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const row = within(await screen.findByTestId(`feed-item-${eligible}`));
    const inactiveRow = within(screen.getByTestId(`feed-item-${unavailable}`));
    expect(inactiveRow.queryByRole('button', { name: /^Комментарии/ })).toBeNull();
    expect(server.sent('GET', threadPath)).toHaveLength(0);
    expect(row.queryByText(otherText)).toBeNull();

    await user.click(row.getByRole('button', { name: /^Комментарии/ }));
    expect(await row.findByText(otherText)).toBeVisible();
    expect(row.queryByRole('button', { name: 'Изменить' })).toBeNull();
    expect(row.queryByRole('button', { name: 'Удалить' })).toBeNull();
    expect(row.queryByRole('button', { name: 'Скрыть' })).toBeNull();

    await user.type(row.getByRole('textbox', { name: 'Написать комментарий' }), ownText);
    await user.click(row.getByRole('button', { name: 'Отправить' }));

    expect(await row.findByText(ownText)).toBeVisible();
    expect(row.getByRole('button', { name: 'Изменить' })).toBeVisible();
    expect(row.getByRole('button', { name: 'Удалить' })).toBeVisible();
    expect(row.queryByRole('button', { name: 'Скрыть' })).toBeNull();
    const id: unknown = expect.any(String);
    expect(server.sent('POST', threadPath).map((call) => call.body)).toEqual([
      { commandId: id, commentId: id, text: ownText },
    ]);
  });

  it('refreshes only the named discussion on a live comment and rereads on catch-up', async () => {
    const first = '1c000000-0000-0000-0000-000000000080';
    const second = '1c000000-0000-0000-0000-000000000081';
    const at = '2026-09-26T10:00:00Z';
    const firstPath = `${feedPath}/${first}/comments`;
    const secondPath = `${feedPath}/${second}/comments`;
    const empty = { comments: [], nextBefore: null, postingAllowed: true };
    const server = fakeServer({
      [`GET ${feedPath}`]: {
        ...page([started(9, first, at), started(8, second, at)]),
        commentTargets: [first, second],
      },
      [`GET ${firstPath}`]: empty,
      [`GET ${secondPath}`]: empty,
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    for (const target of [first, second]) {
      const row = within(await screen.findByTestId(`feed-item-${target}`));
      await user.click(row.getByRole('button', { name: /^Комментарии/ }));
      expect(await row.findByText('Комментариев пока нет.')).toBeVisible();
    }

    await flushLiveChange(10, 'comment-posted', { kind: 'feedEntry', id: first });
    expect(server.sent('GET', firstPath)).toHaveLength(2);
    expect(server.sent('GET', secondPath)).toHaveLength(1);
    await flushLiveChange(11, 'comment-posted');
    expect(server.sent('GET', firstPath)).toHaveLength(3);
    expect(server.sent('GET', secondPath)).toHaveLength(2);
  });

  it('does not expose comments when the feed omits comment targets', async () => {
    const command = '1c000000-0000-0000-0000-000000000085';
    const server = fakeServer({
      [`GET ${feedPath}`]: page([started(9, command, '2026-09-26T10:00:00Z')]),
    });
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const row = within(await screen.findByTestId(`feed-item-${command}`));
    expect(row.queryByRole('button', { name: /^Комментарии/ })).toBeNull();
    expect(server.sent('GET', '/comments')).toHaveLength(0);
  });

  it('keeps the comment identity through a lost response and a rate-limited retry', async () => {
    const target = '1c000000-0000-0000-0000-000000000086';
    const at = '2026-09-26T10:00:00Z';
    const threadPath = `${feedPath}/${target}/comments`;
    const text = 'Спасибо за отзыв';
    type Submitted = { commandId: string; commentId: string; text: string };
    let stored: Submitted | null = null;
    let posts = 0;
    const server = fakeServer({
      [`GET ${feedPath}`]: { ...page([started(9, target, at)]), commentTargets: [target] },
      [`GET ${threadPath}`]: () =>
        json(200, {
          comments: stored
            ? [
                {
                  id: stored.commentId,
                  author: 'Вася',
                  text: stored.text,
                  createdAt: at,
                  editedAt: null,
                  canEdit: true,
                  canHide: false,
                },
              ]
            : [],
          nextBefore: null,
        }),
      [`POST ${threadPath}`]: ({ body }) => {
        if (++posts === 2) return json(429);
        if (stored) return json(204);
        stored = body as Submitted;
        throw new Error('Response lost after commit');
      },
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await user.click(await screen.findByRole('button', { name: /^Комментарии/ }));
    const input = await screen.findByRole('textbox', { name: ru.comments.write });
    await user.type(input, text);
    await user.click(screen.getByRole('button', { name: ru.comments.send }));
    expect(await screen.findByRole('alert')).toHaveTextContent(ru.comments.refused);
    expect(input).toHaveValue(text);

    await user.click(screen.getByRole('button', { name: ru.comments.send }));
    expect(await screen.findByRole('alert')).toHaveTextContent(ru.comments.tooOften);
    expect(input).toHaveValue(text);

    await user.click(screen.getByRole('button', { name: ru.comments.send }));
    expect(await screen.findByText(text, { selector: 'p' })).toBeVisible();
    expect(input).toHaveValue('');
    const attempts = server.sent('POST', threadPath).map(({ body }) => body);
    expect(attempts).toHaveLength(3);
    expect(attempts[0]).toEqual(attempts[1]);
    expect(attempts[0]).toEqual(attempts[2]);

    await user.type(input, 'Следующий комментарий');
    notify(10, 'comment-edited');
    await waitFor(() => {
      expect(server.sent('GET', threadPath)).toHaveLength(3);
    });
    expect(input).toHaveValue('Следующий комментарий');
    expect(input).toHaveFocus();
    expect(server.sent('GET', feedPath)).toHaveLength(1);
  });

  it('finishes a moderation refresh before loading more comments from its new cursor', async () => {
    const target = '1c000000-0000-0000-0000-000000000088';
    const at = '2026-09-26T10:00:00Z';
    const threadPath = `${feedPath}/${target}/comments`;
    const first = '1c000000-0000-0000-0000-000000000089';
    const staleCursor = '1c000000-0000-0000-0000-000000000090';
    const refreshedCursor = '1c000000-0000-0000-0000-000000000091';
    const oldest = '1c000000-0000-0000-0000-000000000092';
    const comment = (id: string, text: string) => ({
      id,
      text,
      author: 'Петя',
      createdAt: at,
      editedAt: null,
      canEdit: false,
      canHide: false,
    });
    const moderatedText = 'Сообщение, которое скрыл администратор';
    const historicalText = 'Ранее в обсуждении';
    const head = { comments: [comment(first, 'Новейший комментарий')], nextBefore: first };
    const cursors: string[] = [];
    let hidden = false;
    let finishHead!: (response: Response) => void;
    const delayedHead = new Promise<Response>((resolve) => {
      finishHead = resolve;
    });
    const server = fakeServer({
      [`GET ${feedPath}`]: { ...page([started(9, target, at)]), commentTargets: [target] },
      [`GET ${threadPath}`]: ({ query }) => {
        const before = query.get('before');
        if (before === null) return hidden ? delayedHead : json(200, head);
        cursors.push(before);
        if (before === first)
          return json(
            200,
            hidden
              ? {
                  comments: [comment(refreshedCursor, 'Другой старый комментарий')],
                  nextBefore: refreshedCursor,
                }
              : { comments: [comment(staleCursor, moderatedText)], nextBefore: staleCursor },
          );
        return json(200, { comments: [comment(oldest, historicalText)], nextBefore: oldest });
      },
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await user.click(await screen.findByRole('button', { name: /^Комментарии/ }));
    await user.click(await screen.findByRole('button', { name: ru.comments.older }));
    expect(await screen.findByText(moderatedText)).toBeVisible();
    const input = screen.getByRole('textbox', { name: ru.comments.write });
    await user.type(input, 'Черновик');

    hidden = true;
    notify(10, 'comment-hidden');
    await waitFor(() => {
      expect(
        server.sent('GET', threadPath).filter(({ query }) => !query.has('before')),
      ).toHaveLength(2);
    });
    const more = screen.getByRole('button', { name: ru.comments.older });
    await user.click(more);
    expect(cursors).toEqual([first]);
    finishHead(json(200, head));

    expect(await screen.findByText(historicalText)).toBeVisible();
    expect(screen.queryByText(moderatedText)).toBeNull();
    expect(cursors).toEqual([first, first, refreshedCursor]);
    expect(input).toHaveValue('Черновик');
    expect(more).toHaveFocus();
  });

  it('keeps active edits through a live read and unlocks the composer once that row is removed', async () => {
    const target = '1c000000-0000-0000-0000-000000000093';
    const at = '2026-09-26T10:00:00Z';
    const path = `${feedPath}/${target}/comments`;
    const own = {
      id: '1c000000-0000-0000-0000-000000000094',
      author: 'Вася',
      text: 'Старый текст',
      createdAt: at,
      editedAt: null,
      canEdit: true,
      canHide: false,
    };
    let hidden = false;
    const server = fakeServer({
      [`GET ${feedPath}`]: { ...page([started(9, target, at)]), commentTargets: [target] },
      [`GET ${path}`]: () => json(200, { comments: hidden ? [] : [own], nextBefore: null }),
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await user.click(await screen.findByRole('button', { name: /^Комментарии/ }));
    await user.click(await screen.findByRole('button', { name: ru.comments.edit }));
    const editing = screen.getByRole('textbox', { name: ru.comments.editLabel });
    await user.clear(editing);
    await user.type(editing, 'Моя незавершённая правка');
    notify(10, 'comment-posted');
    await waitFor(() => {
      expect(server.sent('GET', path)).toHaveLength(2);
    });
    expect(editing).toHaveValue('Моя незавершённая правка');
    expect(editing).toHaveFocus();

    hidden = true;
    await flushLiveChange(11, 'comment-hidden');
    expect(await screen.findByText(ru.comments.empty)).toBeVisible();
    expect(screen.queryByRole('textbox', { name: ru.comments.editLabel })).toBeNull();
    const input = screen.getByRole('textbox', { name: ru.comments.write });
    await user.type(input, 'Новый комментарий');
    expect(screen.getByRole('button', { name: ru.comments.send })).toBeEnabled();
  });

  it('discovers comment eligibility for older loaded rows after enabling the feature', async () => {
    const older = '1c000000-0000-0000-0000-000000000087';
    const olderPage = page([started(4, older, '2026-09-20T10:00:00Z')]);
    let enabled = false;
    const server = fakeServer({
      [`GET ${feedPath}`]: ({ query }) => {
        if (query.has('before'))
          return json(200, enabled ? { ...olderPage, commentTargets: [older] } : olderPage);
        const head = page([started(9, 'new', '2026-09-26T10:00:00Z')], 9);
        return json(200, enabled ? { ...head, commentTargets: [] } : head);
      },
    });
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await userEvent.click(await screen.findByTestId('feed-more'));
    const row = within(await screen.findByTestId(`feed-item-${older}`));
    expect(row.queryByRole('button', { name: /^Комментарии/ })).toBeNull();

    enabled = true;
    notify(10, 'ruleset-changed');
    expect(await row.findByRole('button', { name: /^Комментарии/ })).toBeVisible();
    expect(server.sent('GET', '/comments')).toHaveLength(0);

    enabled = false;
    await flushLiveChange(11, 'ruleset-changed');
    expect(row.queryByRole('button', { name: /^Комментарии/ })).toBeNull();
  });

  it('adds and removes a reaction on an older row from the keyboard without losing its place', async () => {
    const older = '1c000000-0000-0000-0000-000000000002';
    const newer = '1c000000-0000-0000-0000-000000000003';
    const reactionPath = `${feedPath}/${older}/reactions`;
    let selected = false;
    const olderReactions = () => ({ [older]: [fire(selected ? 2 : 1, selected)] });
    const server = fakeServer({
      [`GET ${feedPath}`]: ({ query }) =>
        json(
          200,
          query.has('before')
            ? { ...page([started(4, older, '2026-09-20T10:00:00Z')]), reactions: olderReactions() }
            : { ...page([started(9, newer, '2026-09-26T10:00:00Z')], 9), reactions: {} },
        ),
      [`GET ${feedPath}/reactions`]: () => json(200, olderReactions()),
      [`PUT ${reactionPath}`]: ({ body }) => {
        selected = (body as { selected: boolean }).selected;
        return json(204);
      },
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await user.click(await screen.findByTestId('feed-more'));
    const row = await screen.findByTestId(`feed-item-${older}`);
    const button = within(row).getByRole('button', { name: fireName, pressed: false });
    const scrollTo = vi.spyOn(window, 'scrollTo');
    scrollTo.mockClear();
    try {
      button.focus();
      await user.keyboard('{Enter}');
      const pressed = await within(row).findByRole('button', { name: fireName, pressed: true });
      expect(pressed).toHaveAccessibleDescription('2 реакции');
      expect(pressed).toHaveFocus();
      await waitFor(() => {
        expect(pressed).not.toHaveAttribute('aria-disabled', 'true');
      });

      await user.keyboard(' ');
      const released = await within(row).findByRole('button', { name: fireName, pressed: false });
      expect(released).toHaveAccessibleDescription('1 реакция');
      expect(released).toHaveFocus();
      expect(screen.getByTestId(`feed-item-${newer}`)).toBeVisible();
      expect(scrollTo).not.toHaveBeenCalled();

      const commandId: unknown = expect.any(String);
      const bodies = server.sent('PUT', reactionPath).map(({ body }) => body);
      expect(bodies).toEqual([
        { commandId, emoji: '🔥', selected: true },
        { commandId, emoji: '🔥', selected: false },
      ]);
      const [add, remove] = bodies.map((body) => (body as { commandId: string }).commandId);
      expect(add).not.toBe(remove);
    } finally {
      scrollTo.mockRestore();
    }
  });

  it('keeps only active reactions inline and returns keyboard focus to the collapsed chooser', async () => {
    const target = '1c000000-0000-0000-0000-000000000020';
    const targetReactions = `${feedPath}/${target}/reactions`;
    const selected = new Set(['🔥']);
    const reactions = () => ({
      [target]: [
        fire(selected.has('🔥') ? 1 : 0, selected.has('🔥')),
        { emoji: '❤️', count: selected.has('❤️') ? 1 : 0, selected: selected.has('❤️') },
        { emoji: '😂', count: 2, selected: false },
        { emoji: '👍', count: 0, selected: false },
      ],
    });
    fakeServer({
      [`GET ${feedPath}`]: () =>
        json(200, {
          ...page([started(9, target, '2026-09-26T10:00:00Z')]),
          reactions: reactions(),
          commentTargets: [target],
        }),
      [`GET ${feedPath}/reactions`]: () => json(200, reactions()),
      [`PUT ${targetReactions}`]: ({ body }) => {
        const change = body as { emoji: string; selected: boolean };
        if (change.selected) selected.add(change.emoji);
        else selected.delete(change.emoji);
        return json(204);
      },
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const row = within(await screen.findByTestId(`feed-item-${target}`));
    const group = within(row.getByRole('group', { name: groupName }));
    const add = group.getByRole('button', { name: 'Добавить реакцию' });
    const comments = row.getByRole('button', { name: /^Комментарии к записи/ });
    const heart = 'Реакция ❤️';
    expect(group.getByRole('button', { name: fireName, pressed: true })).toBeVisible();
    expect(group.getByRole('button', { name: 'Реакция 😂' })).toHaveAccessibleDescription(
      '2 реакции',
    );
    expect(group.queryByRole('button', { name: heart })).toBeNull();
    expect(group.getAllByRole('button')).toHaveLength(3);

    add.focus();
    await user.tab();
    expect(comments).toHaveFocus();
    await user.tab({ shift: true });
    expect(add).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(add).toHaveAttribute('aria-expanded', 'true');
    await user.tab();
    expect(group.getByRole('button', { name: heart })).toHaveFocus();
    await user.keyboard('{Escape}');
    expect(add).toHaveAttribute('aria-expanded', 'false');
    expect(add).toHaveFocus();
    expect(group.queryByRole('button', { name: heart })).toBeNull();

    await user.keyboard('{Enter}');
    await user.tab();
    await user.keyboard('{Enter}');
    expect(await group.findByRole('button', { name: heart, pressed: true })).toBeVisible();
    expect(add).toHaveFocus();
    expect(add).toHaveAttribute('aria-expanded', 'false');

    const fireButton = group.getByRole('button', { name: fireName, pressed: true });
    await waitFor(() => {
      expect(fireButton).not.toHaveAttribute('aria-disabled', 'true');
    });
    fireButton.focus();
    await user.keyboard(' ');
    await waitFor(() => {
      expect(group.queryByRole('button', { name: fireName })).toBeNull();
      expect(add).toHaveFocus();
    });
    await user.tab();
    expect(comments).toHaveFocus();
  });

  it('focuses the newly added reaction when the last hidden choice becomes visible', async () => {
    const target = '1c000000-0000-0000-0000-000000000021';
    let selected = false;
    const reactions = () => ({ [target]: [fire(selected ? 1 : 0, selected)] });
    fakeServer({
      [`GET ${feedPath}`]: () =>
        json(200, {
          ...page([started(9, target, '2026-09-26T10:00:00Z')]),
          reactions: reactions(),
        }),
      [`GET ${feedPath}/reactions`]: () => json(200, reactions()),
      [`PUT ${feedPath}/${target}/reactions`]: () => {
        selected = true;
        return json(204);
      },
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const group = within(await screen.findByRole('group', { name: groupName }));
    await user.click(group.getByRole('button', { name: 'Добавить реакцию' }));
    await user.tab();
    await user.keyboard('{Enter}');
    const fireButton = await group.findByRole('button', { name: fireName, pressed: true });
    expect(group.queryByRole('button', { name: 'Добавить реакцию' })).toBeNull();
    await waitFor(() => {
      expect(fireButton).toHaveFocus();
    });
  });

  it('only offers eligible reactions and removes the controls after undo or disabling the feature', async () => {
    const at = '2026-09-26T10:00:00Z';
    const eligible = '1c000000-0000-0000-0000-000000000004';
    const other = '1c000000-0000-0000-0000-000000000005';
    let enabled = false;
    let undone = false;
    const reactions = () => (enabled && !undone ? { [eligible]: [fire(0)] } : {});
    fakeServer({
      [`GET ${feedPath}`]: () => {
        const current = page([started(8, eligible, at, undone), started(7, other, at)]);
        return json(200, enabled ? { ...current, reactions: reactions() } : current);
      },
      [`GET ${feedPath}/reactions`]: () => json(200, reactions()),
    });
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId(`feed-item-${eligible}`);
    expect(screen.queryByRole('group', { name: groupName })).toBeNull();

    enabled = true;
    notify(9, 'ruleset-changed');
    expect(await screen.findByRole('group', { name: groupName })).toBeVisible();
    expect(
      within(screen.getByTestId(`feed-item-${other}`)).queryByRole('group', { name: groupName }),
    ).toBeNull();

    enabled = false;
    await flushLiveChange(10, 'ruleset-changed');
    await waitFor(() => {
      expect(screen.queryByRole('group', { name: groupName })).toBeNull();
    });

    enabled = true;
    await flushLiveChange(11, 'ruleset-changed');
    expect(await screen.findByRole('group', { name: groupName })).toBeVisible();

    undone = true;
    await flushLiveChange(12, 'command-undone');
    await waitFor(() => {
      expect(screen.getByTestId(`feed-item-${eligible}`)).toHaveAttribute('data-undone', 'true');
      expect(screen.queryByRole('group', { name: groupName })).toBeNull();
    });
  });

  it('updates an already loaded older row after a reaction in another session', async () => {
    const older = '1c000000-0000-0000-0000-000000000006';
    let count = 1;
    const olderReactions = () => ({ [older]: [fire(count)] });
    const server = fakeServer({
      [`GET ${feedPath}`]: ({ query }) =>
        json(
          200,
          query.has('before')
            ? { ...page([started(4, older, '2026-09-20T10:00:00Z')]), reactions: olderReactions() }
            : { ...page([started(9, 'new', '2026-09-26T10:00:00Z')], 9), reactions: {} },
        ),
      [`GET ${feedPath}/reactions`]: ({ query }) =>
        json(200, query.getAll('commandIds').includes(older) ? olderReactions() : {}),
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await user.click(await screen.findByTestId('feed-more'));
    const row = await screen.findByTestId(`feed-item-${older}`);
    const button = within(row).getByRole('button', { name: fireName, pressed: false });
    expect(button).toHaveAccessibleDescription('1 реакция');
    button.focus();

    count = 2;
    notify(10, 'feed-reaction-set');
    await waitFor(() => {
      expect(
        within(row).getByRole('button', { name: fireName, pressed: false }),
      ).toHaveAccessibleDescription('2 реакции');
    });
    expect(within(row).getByRole('button', { name: fireName })).toHaveFocus();
    expect(server.calls.some(({ method }) => method === 'PUT')).toBe(false);
  });

  it('coalesces a burst of hub updates and refreshes older reactions at the trailing edge', async () => {
    const older = '1c000000-0000-0000-0000-000000000010';
    let count = 1;
    const olderReactions = () => ({ [older]: [fire(count)] });
    const batchPath = `${feedPath}/reactions`;
    const server = fakeServer({
      [`GET ${feedPath}`]: ({ query }) =>
        json(
          200,
          query.has('before')
            ? { ...page([started(4, older, '2026-09-20T10:00:00Z')]), reactions: olderReactions() }
            : { ...page([started(9, 'new', '2026-09-26T10:00:00Z')], 9), reactions: {} },
        ),
      [`GET ${batchPath}`]: ({ query }) =>
        json(200, query.getAll('commandIds').includes(older) ? olderReactions() : {}),
    });
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await userEvent.click(await screen.findByTestId('feed-more'));
    const button = await screen.findByRole('button', { name: fireName });
    expect(button).toHaveAccessibleDescription('1 реакция');

    const advance = async (ms: number) => {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(ms);
      });
    };
    vi.useFakeTimers();
    try {
      count = 2;
      notify(10, 'feed-reaction-set');
      await advance(0);
      expect(button).toHaveAccessibleDescription('2 реакции');
      expect(server.sent('GET', feedPath)).toHaveLength(3);
      expect(server.sent('GET', batchPath)).toHaveLength(1);

      count = 3;
      notify(11, 'feed-reaction-set');
      await advance(1000);
      count = 4;
      notify(12, 'feed-reaction-set');
      await advance(1000);
      expect(button).toHaveAccessibleDescription('2 реакции');
      expect(server.sent('GET', feedPath)).toHaveLength(3);
      expect(server.sent('GET', batchPath)).toHaveLength(1);

      await advance(1000);
      expect(button).toHaveAccessibleDescription('4 реакции');
      expect(server.sent('GET', feedPath)).toHaveLength(4);
      expect(server.sent('GET', batchPath)).toHaveLength(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps an acknowledged reaction when its refresh fails and an older live read arrives late', async () => {
    const older = '1c000000-0000-0000-0000-000000000007';
    const olderReactions = (count: number, selected = false) => ({
      [older]: [fire(count, selected)],
    });
    let finishLiveRead!: (response: Response) => void;
    const delayedLiveRead = new Promise<Response>((resolve) => {
      finishLiveRead = resolve;
    });
    let recovered = false;
    let written = false;
    const batchPath = `${feedPath}/reactions`;
    const server = fakeServer({
      [`GET ${feedPath}`]: ({ query }) =>
        json(
          200,
          query.has('before')
            ? { ...page([started(4, older, '2026-09-20T10:00:00Z')]), reactions: olderReactions(1) }
            : { ...page([started(9, 'new', '2026-09-26T10:00:00Z')], 9), reactions: {} },
        ),
      [`GET ${batchPath}`]: () => {
        if (recovered) return json(200, olderReactions(5));
        return written ? json(500) : delayedLiveRead;
      },
      [`PUT ${feedPath}/${older}/reactions`]: () => {
        written = true;
        return json(204);
      },
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await user.click(await screen.findByTestId('feed-more'));
    const button = await screen.findByRole('button', { name: fireName, pressed: false });

    notify(10, 'feed-reaction-set');
    await waitFor(() => {
      expect(server.sent('GET', batchPath)).toHaveLength(1);
    });
    await user.click(button);
    expect(await screen.findByText(ru.gamePage.reviewReactions.stale)).toBeVisible();
    expect(button).toHaveAttribute('aria-pressed', 'true');
    expect(button).toHaveAccessibleDescription('2 реакции');

    await act(async () => {
      finishLiveRead(json(200, olderReactions(1)));
      await delayedLiveRead;
    });
    expect(button).toHaveAttribute('aria-pressed', 'true');
    expect(button).toHaveAccessibleDescription('2 реакции');
    expect(button).toHaveFocus();

    recovered = true;
    await flushLiveChange(11, 'feed-reaction-set');
    await waitFor(() => expect(button).toHaveAccessibleDescription('5 реакций'));
    expect(button).toHaveAttribute('aria-pressed', 'false');
    expect(button).toHaveFocus();
    expect(screen.queryByText(ru.gamePage.reviewReactions.stale)).toBeNull();
  });

  it('keeps reactions hidden when a page requested before disabling them arrives late', async () => {
    const newer = '1c000000-0000-0000-0000-000000000008';
    const older = '1c000000-0000-0000-0000-000000000009';
    let finishOlderRead!: (response: Response) => void;
    const delayedOlderRead = new Promise<Response>((resolve) => {
      finishOlderRead = resolve;
    });
    let enabled = true;
    const server = fakeServer({
      [`GET ${feedPath}`]: ({ query }) => {
        if (query.has('before')) {
          return enabled
            ? delayedOlderRead
            : json(200, page([started(4, older, '2026-09-20T10:00:00Z')]));
        }
        const newest = page([started(9, newer, '2026-09-26T10:00:00Z')], 9);
        return json(200, enabled ? { ...newest, reactions: { [newer]: [fire(1)] } } : newest);
      },
    });
    const user = userEvent.setup();
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    expect(await screen.findByRole('group', { name: groupName })).toBeVisible();
    await user.click(screen.getByTestId('feed-more'));
    await waitFor(() => {
      expect(server.sent('GET', feedPath).some(({ query }) => query.has('before'))).toBe(true);
    });

    enabled = false;
    notify(10, 'ruleset-changed');
    await waitFor(() => {
      expect(screen.queryByRole('group', { name: groupName })).toBeNull();
    });

    finishOlderRead(
      json(200, {
        ...page([started(4, older, '2026-09-20T10:00:00Z')]),
        reactions: { [older]: [fire(1)] },
      }),
    );
    expect(await screen.findByTestId(`feed-item-${older}`)).toBeVisible();
    expect(screen.queryByRole('group', { name: groupName })).toBeNull();
    expect(server.sent('GET', feedPath).filter(({ query }) => query.has('before'))).toHaveLength(2);
  });

  it('rereads an in-flight older cursor after a new head marks its target undone', async () => {
    const older = '1c000000-0000-0000-0000-000000000011';
    const undo = undoSummary(older);
    const at = '2026-09-20T10:00:00Z';
    let finishStalePage!: (response: Response) => void;
    const stalePage = new Promise<Response>((resolve) => {
      finishStalePage = resolve;
    });
    let undone = false;
    const cursors: string[] = [];
    fakeServer({
      [`GET ${feedPath}`]: ({ query }) => {
        const cursor = query.get('before');
        if (cursor !== null) {
          cursors.push(cursor);
          if (cursors.length === 1) return stalePage;
          return json(200, { ...page([started(4, older, at, undone)]), reactions: {} });
        }
        const entries = [started(9, 'new', '2026-09-26T10:00:00Z')];
        if (undone) entries.unshift(undo);
        return json(200, { ...page(entries, 9), reactions: {} });
      },
    });
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await userEvent.click(await screen.findByTestId('feed-more'));
    await waitFor(() => {
      expect(cursors).toEqual(['9']);
    });

    undone = true;
    notify(10, 'command-undone');
    await screen.findByTestId(`feed-item-${undo.commandId}`);
    finishStalePage(
      json(200, {
        ...page([started(4, older, at)]),
        reactions: { [older]: [fire(1)] },
      }),
    );

    const row = await screen.findByTestId(`feed-item-${older}`);
    expect(row).toHaveAttribute('data-undone', 'true');
    expect(row).toHaveTextContent(ru.feed.undone);
    expect(within(row).queryByRole('group', { name: groupName })).toBeNull();
    expect(cursors).toEqual(['9', '9']);
  });

  it('marks an already visible older row undone from the live undo summary', async () => {
    const older = '1c000000-0000-0000-0000-000000000013';
    const undo = undoSummary(older);
    let undone = false;
    fakeServer({
      [`GET ${feedPath}`]: ({ query }) => {
        if (query.has('before')) {
          return json(200, {
            ...page([started(4, older, '2026-09-20T10:00:00Z')]),
            reactions: { [older]: [fire(1)] },
          });
        }
        const entries = [started(9, 'new', '2026-09-26T10:00:00Z')];
        if (undone) entries.unshift(undo);
        return json(200, { ...page(entries, 9), reactions: {} });
      },
      [`GET ${feedPath}/reactions`]: () => json(500),
    });
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await userEvent.click(await screen.findByTestId('feed-more'));
    const row = await screen.findByTestId(`feed-item-${older}`);
    expect(within(row).getByRole('group', { name: groupName })).toBeVisible();

    undone = true;
    notify(10, 'command-undone');
    await screen.findByTestId(`feed-item-${undo.commandId}`);
    expect(row).toHaveAttribute('data-undone', 'true');
    expect(row).toHaveTextContent(ru.feed.undone);
    expect(within(row).queryByRole('group', { name: groupName })).toBeNull();
  });

  it('brings back the older lines shown and the place in the feed after a profile and Back (K1A)', async () => {
    serve((before) =>
      before === null
        ? json(200, page([started(9, 'new', '2026-09-26T10:00:00Z')], 9))
        : json(200, page([started(3, 'old', '2026-09-20T10:00:00Z')])),
    );
    globalThis.history.pushState(null, '', '/feed');
    const first = render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await userEvent.click(await screen.findByTestId('feed-more'));
    expect(await screen.findByTestId('feed-item-old')).toBeInTheDocument();

    Object.defineProperty(window, 'scrollY', { configurable: true, value: 5116 });
    navigate('/users/aaaaaaaa-0000-0000-0000-000000000001');
    first.unmount();
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 0 });
    const scrolled = vi.spyOn(window, 'scrollTo');
    await new Promise<void>((resolve) => {
      globalThis.addEventListener(
        'popstate',
        () => {
          resolve();
        },
        { once: true },
      );
      globalThis.history.back();
    });
    render(<FeedScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('feed-item-old')).toBeInTheDocument();
    await waitFor(() => {
      expect(scrolled).toHaveBeenCalledWith(0, 5116);
    });
    scrolled.mockRestore();
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
