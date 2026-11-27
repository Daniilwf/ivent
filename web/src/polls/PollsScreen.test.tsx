import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { refreshCsrf, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, json, type Handler } from '../test/fakeServer';
import { PollsScreen } from './PollsScreen';

type Poll = Schemas['PollView'];
type Update = Schemas['SeasonUpdate'];

const live = vi.hoisted(() => ({
  deferJoin: false,
  sequence: 0,
  subscriptions: [] as {
    seasonId: string;
    change: (updates: Update[]) => void;
    stopped: boolean;
  }[],
}));

vi.mock('../api/realtime', () => ({
  watchSeason: (seasonId: string, change: (updates: Update[]) => void) => {
    const subscription = { seasonId, change, stopped: false };
    live.subscriptions.push(subscription);
    if (!live.deferJoin) change([]);
    return () => {
      subscription.stopped = true;
    };
  },
}));

const seasonA = '56576278-6eb9-4242-9bc4-122ac784de90';
const seasonB = '6405f9de-29bc-4774-94ef-19b92bf8380f';
const firstPollId = 'e9a2c8c5-301f-4c86-9ed3-128592970f4d';
const secondPollId = 'daff1d08-f79a-4653-ad98-848373fe36ca';
const horrorId = '0c6969ce-f859-42c8-8221-73357bbd78f3';
const strategyId = '6072d00f-6829-4009-bfcc-6f511b42c619';
const firstQuestion = 'Какой жанр сыграем вместе?';
const listPath = (season: string) => `/api/seasons/${season}/polls`;
const votePath = (season: string, id = firstPollId) => `${listPath(season)}/${id}/vote`;
const accepted: Schemas['CommandResponse'] = {
  duplicate: false,
  events: [{ sequence: 10, type: 'vote-cast' }],
};

function poll(overrides: Partial<Poll> = {}): Poll {
  return {
    id: firstPollId,
    question: firstQuestion,
    options: [
      { id: horrorId, label: 'Хорроры' },
      { id: strategyId, label: 'Стратегии' },
    ],
    voters: 'players',
    anonymous: false,
    status: 'open',
    startedAt: '2030-11-17T12:00:00Z',
    closesAt: '2030-11-24T12:00:00Z',
    counts: [
      { optionId: horrorId, votes: 2 },
      { optionId: strategyId, votes: 0 },
    ],
    result: null,
    myVote: null,
    source: 'admin',
    voting: { canVote: true, reason: null, allowChangeVote: true },
    ...overrides,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

function serve(
  read: Handler | readonly Poll[] = [poll()],
  write: Handler = () => json(200, accepted),
) {
  const get: Handler = typeof read === 'function' ? read : () => json(200, read);
  return fakeServer({
    [`GET ${listPath(seasonA)}`]: get,
    [`GET ${listPath(seasonB)}`]: get,
    'POST /api/seasons/*/polls/*/vote': write,
  });
}

function push(types: string[], seasonId = seasonA) {
  const sequence = ++live.sequence;
  const update: Update = {
    seasonId,
    fromSequence: sequence,
    toSequence: sequence,
    types,
  };
  act(() => {
    for (const subscription of live.subscriptions)
      if (!subscription.stopped) subscription.change([update]);
  });
}

function reloadFromHub() {
  act(() => {
    for (const subscription of live.subscriptions)
      if (!subscription.stopped) subscription.change([]);
  });
}

function radio(label: string) {
  return screen.getByRole('radio', { name: label });
}

function expectNoEnabledVote() {
  for (const option of screen.queryAllByRole('radio')) expect(option).toBeDisabled();
  for (const action of screen.queryAllByRole('button', {
    name: /^(Голосовать|Изменить голос)$/,
  }))
    expect(action).toBeDisabled();
}

describe('the season polls screen (EV21/EV22/EV24/EV39)', () => {
  beforeEach(() => {
    live.deferJoin = false;
    live.sequence = 0;
    live.subscriptions.length = 0;
  });
  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    globalThis.history.replaceState(null, '', '/');
  });

  it('starts with loading and reads the list once for open, closed and cancelled cards without card requests', async () => {
    const waiting = deferred<Response>();
    const server = serve(() => waiting.promise);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);

    expect(screen.getByRole('heading', { level: 1, name: 'Голосования' })).toBeVisible();
    expect(screen.getByTestId('polls-loading')).toHaveAttribute('aria-busy', 'true');
    await act(async () => {
      waiting.resolve(
        json(200, [
          poll(),
          poll({
            id: secondPollId,
            question: 'Закрытый вопрос',
            status: 'closed',
            result: strategyId,
            voting: {
              canVote: false,
              reason: 'poll.closed',
              allowChangeVote: true,
            },
          }),
          poll({
            id: '03953cf8-a1d0-40c2-b8d8-c9100fc34704',
            question: 'Отменённый вопрос',
            status: 'cancelled',
            counts: null,
            voting: {
              canVote: false,
              reason: 'poll.closed',
              allowChangeVote: true,
            },
          }),
        ]),
      );
      await waiting.promise;
    });

    expect(await screen.findByRole('heading', { name: firstQuestion })).toBeVisible();
    const open = screen.getByRole('heading', { name: 'Открытые' });
    const complete = screen.getByRole('heading', { name: 'Завершённые' });
    const first = screen.getByRole('heading', { name: firstQuestion });
    const closed = screen.getByRole('heading', { name: 'Закрытый вопрос' });
    expect(open.compareDocumentPosition(first) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(first.compareDocumentPosition(complete) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(
      complete.compareDocumentPosition(closed) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(screen.getByText('Итог: Стратегии')).toBeVisible();
    expect(screen.getByText('Голосование отменено')).toBeVisible();
    expect(screen.queryByTestId('polls-loading')).not.toBeInTheDocument();
    expect(server.calls.filter((call) => call.method === 'GET').map((call) => call.path)).toEqual([
      listPath(seasonA),
    ]);
  });

  it('shows an empty state once, without empty status groups or voting controls', async () => {
    const server = serve([]);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: 'Пока нет голосований' })).toBeVisible();
    expect(screen.getAllByText('Пока нет голосований')).toHaveLength(1);
    expect(screen.queryByRole('heading', { name: 'Открытые' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Завершённые' })).not.toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    expect(server.sent('GET', '/polls')).toHaveLength(1);
  });

  it.each(['offline', 'server', 'rate-limit'] as const)(
    'offers an explicit read retry after an initial %s failure',
    async (failure) => {
      let failed = true;
      const server = serve(() => {
        if (!failed) return json(200, [poll()]);
        if (failure === 'offline') throw new TypeError('private transport diagnostics');
        return json(failure === 'server' ? 500 : 429, {
          title: 'private server diagnostics',
        });
      });
      const { container } = render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);

      expect(await screen.findByRole('alert')).toHaveTextContent(
        'Не удалось загрузить голосования',
      );
      expect(container).not.toHaveTextContent(/private.*diagnostics/);
      expectNoEnabledVote();
      failed = false;
      await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

      expect(await screen.findByRole('heading', { name: firstQuestion })).toBeVisible();
      expect(server.sent('GET', '/polls')).toHaveLength(2);
      expect(server.sent('POST', '/vote')).toHaveLength(0);
    },
  );

  it('distinguishes an unknown season from an empty poll list', async () => {
    serve(() => json(404));
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: 'Такого сезона нет' })).toBeVisible();
    expect(screen.queryByText('Пока нет голосований')).not.toBeInTheDocument();
    expectNoEnabledVote();
  });

  it('explains a disabled mechanic without exposing enabled voting controls', async () => {
    serve(() => json(409, { code: 'feature.disabled' }));
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: 'Голосования выключены' })).toBeVisible();
    expect(screen.queryByText('Пока нет голосований')).not.toBeInTheDocument();
    expectNoEnabledVote();
  });

  it('hands an unauthorized initial read to sign-in once', async () => {
    const onSignedOut = vi.fn();
    const server = serve(() => json(401));
    render(<PollsScreen seasonId={seasonA} onSignedOut={onSignedOut} />);

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalledOnce();
    });
    expect(server.sent('GET', '/polls')).toHaveLength(1);
    expectNoEnabledVote();
  });

  it('sends one selected option to the current season and poll through the command client with CSRF', async () => {
    let saved = false;
    const server = serve(
      () => json(200, [poll({ myVote: saved ? strategyId : null })]),
      () => {
        saved = true;
        return json(200, accepted);
      },
    );
    await refreshCsrf();
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    expect(server.sent('POST', '/vote')).toHaveLength(0);
    await userEvent.click(screen.getByRole('button', { name: 'Голосовать' }));

    await waitFor(() => {
      expect(server.sent('GET', '/polls')).toHaveLength(2);
    });
    const sent = server.sent('POST', '/vote');
    expect(sent).toHaveLength(1);
    expect(sent[0]?.path).toBe(votePath(seasonA));
    expect(sent[0]?.body).toEqual(
      expect.objectContaining({
        optionId: strategyId,
        commandId: expect.stringMatching(
          /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i,
        ) as unknown,
      }),
    );
    expect(sent[0]?.body).not.toHaveProperty('playerId');
    expect(sent[0]?.body).not.toHaveProperty('userId');
    expect(sent[0]?.body).not.toHaveProperty('commandId', '00000000-0000-0000-0000-000000000000');
    const request = server.fetch.mock.calls.find(([value]) => value.method === 'POST')?.[0];
    expect(request?.headers.get('X-CSRF-TOKEN')).toBe('t');
    expect(await screen.findByRole('button', { name: 'Изменить голос' })).toBeDisabled();
  });

  it('retains the cards and draft while a command waits, without inventing a recorded vote before acceptance', async () => {
    const answer = deferred<Response>();
    const server = serve([poll()], () => answer.promise);
    await refreshCsrf();
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    await userEvent.dblClick(screen.getByRole('button', { name: 'Голосовать' }));

    try {
      await waitFor(() => {
        expect(server.sent('POST', '/vote')).toHaveLength(1);
      });
      expect(screen.getByRole('heading', { name: firstQuestion })).toBeVisible();
      expect(radio('Стратегии')).toBeChecked();
      expect(radio('Стратегии')).toBeDisabled();
      expect(screen.getByText('0 голосов')).toBeVisible();
      expect(screen.queryByText('Твой голос: Стратегии')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Изменить голос' })).not.toBeInTheDocument();
      expect(server.sent('GET', '/polls')).toHaveLength(1);
    } finally {
      await act(async () => {
        answer.resolve(json(409, { code: 'poll.closed' }));
        await answer.promise;
      });
    }
  });

  it('keeps the previous cards and unsent draft after a rejected write and allows another deliberate attempt', async () => {
    const server = serve([poll()], () => json(409, { code: 'poll.unknownOption' }));
    await refreshCsrf();
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    await userEvent.click(screen.getByRole('button', { name: 'Голосовать' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.polls.voteFailed);
    expect(screen.getByRole('heading', { name: firstQuestion })).toBeVisible();
    expect(radio('Стратегии')).toBeChecked();
    expect(screen.getByRole('button', { name: 'Голосовать' })).toBeEnabled();
    expect(server.sent('POST', '/vote')).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Голосовать' }));
    await waitFor(() => {
      expect(server.sent('POST', '/vote')).toHaveLength(2);
    });
    expect(radio('Стратегии')).toBeChecked();
  });

  it('hands an unauthorized write to sign-in without pretending it succeeded', async () => {
    const onSignedOut = vi.fn();
    const server = serve([poll()], () => json(401));
    await refreshCsrf();
    render(<PollsScreen seasonId={seasonA} onSignedOut={onSignedOut} />);
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    await userEvent.click(screen.getByRole('button', { name: 'Голосовать' }));

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalledOnce();
    });
    expect(screen.queryByText('Голос принят')).not.toBeInTheDocument();
    expect(server.sent('POST', '/vote')).toHaveLength(1);
  });

  it('truthfully reports an accepted vote when its following read fails and retries only that read', async () => {
    let reads = 0;
    const server = serve(() => {
      reads++;
      if (reads === 2) return json(500);
      return json(200, [poll({ myVote: reads >= 3 ? strategyId : null })]);
    });
    await refreshCsrf();
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    await userEvent.click(screen.getByRole('button', { name: 'Голосовать' }));

    expect(await screen.findByText('Голос принят. Не удалось обновить список.')).toBeVisible();
    expect(screen.queryByText(ru.polls.voteFailed)).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: firstQuestion })).toBeVisible();
    expect(server.sent('POST', '/vote')).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Обновить список' }));

    expect(await screen.findByRole('button', { name: 'Изменить голос' })).toBeDisabled();
    expect(server.sent('POST', '/vote')).toHaveLength(1);
    expect(server.sent('GET', '/polls')).toHaveLength(3);
    expect(screen.queryByText('Голос принят. Не удалось обновить список.')).not.toBeInTheDocument();
  });

  it('retains readable cards and an edited draft when a live refresh fails', async () => {
    let failed = false;
    const server = serve(() => (failed ? json(500) : json(200, [poll()])));
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    failed = true;
    push(['vote-cast']);

    expect(await screen.findByRole('alert')).toHaveTextContent('Не удалось загрузить голосования');
    expect(screen.getByRole('heading', { name: firstQuestion })).toBeVisible();
    expect(radio('Стратегии')).toBeChecked();
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));
    await waitFor(() => {
      expect(server.sent('GET', '/polls')).toHaveLength(3);
    });
    expect(radio('Стратегии')).toBeChecked();
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it.each([
    'vote-cast',
    'vote-changed',
    'poll-started',
    'poll-closed',
    'poll-cancelled',
    'ruleset-changed',
    'command-undone',
    'season-player-added',
    'player-finished',
    'player-finish-revoked',
    'season-status-changed',
  ])('refreshes the list after relevant hub event %s without a write', async (type) => {
    const server = serve([poll()]);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    push([type]);

    await waitFor(() => {
      expect(server.sent('GET', '/polls')).toHaveLength(2);
    });
    expect(screen.getByRole('heading', { name: firstQuestion })).toBeVisible();
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it('refreshes an anonymous vote change arriving as vote-cast while preserving the draft and hidden counts', async () => {
    const server = serve([poll({ anonymous: true, counts: null })]);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    push(['vote-cast']);

    await waitFor(() => {
      expect(server.sent('GET', '/polls')).toHaveLength(2);
    });
    expect(radio('Стратегии')).toBeChecked();
    expect(screen.getByText('Счёт скрыт до закрытия')).toBeVisible();
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it('refreshes voting metadata after an empty reconnect notification without needing an event type', async () => {
    let current = poll();
    const server = serve(() => json(200, [current]));
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    expect(server.sent('GET', '/polls')).toHaveLength(1);
    current = poll({
      voting: { canVote: false, reason: 'poll.notVoter', allowChangeVote: true },
    });

    reloadFromHub();

    expect(
      await screen.findByText('В этом голосовании голосуют только игроки сезона.'),
    ).toBeVisible();
    expectNoEnabledVote();
    expect(server.sent('GET', '/polls')).toHaveLength(2);
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it('reloads a poll that closed after the first snapshot and before the initial hub join', async () => {
    live.deferJoin = true;
    let current = poll();
    const server = serve(() => json(200, [current]));
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    expect(radio('Стратегии')).toBeEnabled();
    expect(server.sent('GET', '/polls')).toHaveLength(1);

    current = poll({
      status: 'closed',
      result: horrorId,
      voting: { canVote: false, reason: 'poll.closed', allowChangeVote: true },
    });
    reloadFromHub();

    expect(await screen.findByText('Итог: Хорроры')).toBeVisible();
    expectNoEnabledVote();
    expect(server.sent('GET', '/polls')).toHaveLength(2);
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it('keeps an empty Resume reload received during a read and coalesces it into one fresh metadata read', async () => {
    vi.useFakeTimers();
    const initial = deferred<Response>();
    const following = deferred<Response>();
    let reads = 0;
    const server = serve(() => {
      reads++;
      return reads === 1 ? initial.promise : following.promise;
    });
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(server.sent('GET', '/polls')).toHaveLength(1);

    reloadFromHub();
    reloadFromHub();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(server.sent('GET', '/polls')).toHaveLength(1);
    await act(async () => {
      initial.resolve(json(200, [poll()]));
      await initial.promise;
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(server.sent('GET', '/polls')).toHaveLength(2);
    await act(async () => {
      following.resolve(
        json(200, [
          poll({
            voting: { canVote: false, reason: 'poll.frozenVoter', allowChangeVote: true },
          }),
        ]),
      );
      await following.promise;
      await vi.advanceTimersByTimeAsync(60_000);
    });

    expect(
      screen.getByText('Финишировавший первым не голосует там, где итог действует на игроков.'),
    ).toBeVisible();
    expectNoEnabledVote();
    expect(server.sent('GET', '/polls')).toHaveLength(2);
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it('ignores unrelated event types instead of spending a list read', async () => {
    const server = serve([poll()]);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    push(['run-started', 'item-bought']);
    await act(async () => Promise.resolve());

    expect(server.sent('GET', '/polls')).toHaveLength(1);
  });

  it('ignores another seasons update instead of refreshing the current poll list', async () => {
    const server = serve([poll()]);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });
    push(['vote-cast'], seasonB);
    await act(async () => Promise.resolve());

    expect(server.sent('GET', '/polls')).toHaveLength(1);
  });

  it('stops an unsent vote and moves focus to the same poll heading when it closes live', async () => {
    let closed = false;
    const server = serve(() =>
      json(200, [
        poll(
          closed
            ? {
                status: 'closed',
                result: horrorId,
                voting: {
                  canVote: false,
                  reason: 'poll.closed',
                  allowChangeVote: true,
                },
              }
            : {},
        ),
      ]),
    );
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    const openHeading = await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    expect(radio('Стратегии')).toHaveFocus();
    closed = true;
    push(['poll-closed']);

    expect(await screen.findByText('Итог: Хорроры')).toBeVisible();
    expect(openHeading).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: firstQuestion })).toHaveFocus();
    expectNoEnabledVote();
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it.each(['hidden', 'visible'] as const)(
    'moves an unfinished poll with %s counts to completed when its season finishes live',
    async (visibility) => {
      let current = poll({
        myVote: horrorId,
        ...(visibility === 'hidden' ? { anonymous: true, counts: null } : {}),
      });
      const server = serve(() => json(200, [current]));
      render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
      await screen.findByRole('heading', { name: firstQuestion });
      expect(screen.getByRole('heading', { name: 'Открытые' })).toBeVisible();
      expect(radio('Хорроры')).toBeChecked();
      await userEvent.click(radio('Стратегии'));
      expect(radio('Стратегии')).toHaveFocus();

      current = {
        ...current,
        voting: { canVote: false, reason: 'season.closed', allowChangeVote: true },
      };
      push(['season-status-changed']);

      expect(await screen.findByRole('heading', { name: 'Завершённые' })).toBeVisible();
      expect(screen.queryByRole('heading', { name: 'Открытые' })).not.toBeInTheDocument();
      expect(screen.getByRole('heading', { name: firstQuestion })).toHaveFocus();
      expect(screen.getByText('Сезон завершён без итога голосования.')).toBeVisible();
      expect(screen.getByText('Хорроры', { exact: true })).toBeVisible();
      expect(screen.getByText('Стратегии', { exact: true })).toBeVisible();
      expect(screen.getByText('Твой голос: Хорроры')).toBeVisible();
      expect(screen.queryByText('Голосование закрыто')).not.toBeInTheDocument();
      expect(screen.queryByText(/^Итог:/)).not.toBeInTheDocument();
      expect(screen.queryByText('Счёт скрыт до закрытия')).not.toBeInTheDocument();
      expect(screen.queryByText('Счёт появится после подведения итогов.')).not.toBeInTheDocument();
      if (visibility === 'visible') {
        expect(screen.getByText('2 голоса', { exact: true })).toBeVisible();
        expect(screen.getByText('0 голосов', { exact: true })).toBeVisible();
      } else {
        expect(screen.queryByText(/^\d+ голос/)).not.toBeInTheDocument();
      }
      expectNoEnabledVote();
      expect(server.sent('POST', '/vote')).toHaveLength(0);
    },
  );

  it('keeps a poll with an expired ballot in open while its result is still pending', async () => {
    serve([
      poll({
        counts: null,
        voting: { canVote: false, reason: 'poll.closed', allowChangeVote: true },
      }),
    ]);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: 'Открытые' })).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Завершённые' })).not.toBeInTheDocument();
    expect(screen.getByText('Приём голосов завершён.')).toBeVisible();
    expect(screen.getByText('Счёт появится после подведения итогов.')).toBeVisible();
    expectNoEnabledVote();
  });

  it('keeps focus outside the poll if the player moves it while a closing refresh is pending', async () => {
    const closing = deferred<Response>();
    let closed = false;
    const server = serve(() => (closed ? closing.promise : json(200, [poll()])));
    render(
      <>
        <button>Другое действие</button>
        <PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />
      </>,
    );
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    expect(radio('Стратегии')).toHaveFocus();
    closed = true;
    push(['poll-closed']);
    await waitFor(() => {
      expect(server.sent('GET', '/polls')).toHaveLength(2);
    });
    const otherAction = screen.getByRole('button', { name: 'Другое действие' });
    await userEvent.click(otherAction);
    closing.resolve(json(200, [poll({ status: 'closed', result: horrorId })]));

    expect(await screen.findByText('Итог: Хорроры')).toBeVisible();
    expect(otherAction).toHaveFocus();
  });

  it('uses server voting metadata instead of guessing availability from a poll source', async () => {
    const server = serve([
      poll({
        source: 'event',
        anonymous: true,
        counts: null,
        voting: { canVote: true, reason: null, allowChangeVote: true },
      }),
    ]);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await screen.findByRole('heading', { name: firstQuestion });

    await userEvent.click(radio('Стратегии'));

    expect(screen.getByRole('button', { name: 'Голосовать' })).toBeEnabled();
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it('drops a previous seasons late answer after the page switches to a different season', async () => {
    const old = deferred<Response>();
    const server = serve((call) =>
      call.path === listPath(seasonA)
        ? old.promise
        : json(200, [poll({ id: secondPollId, question: 'Вопрос другого сезона' })]),
    );
    const onSignedOut = vi.fn();
    const { rerender } = render(<PollsScreen seasonId={seasonA} onSignedOut={onSignedOut} />);
    await waitFor(() => {
      expect(server.sent('GET', '/polls')).toHaveLength(1);
    });
    rerender(<PollsScreen seasonId={seasonB} onSignedOut={onSignedOut} />);
    expect(await screen.findByRole('heading', { name: 'Вопрос другого сезона' })).toBeVisible();

    await act(async () => {
      old.resolve(json(200, [poll()]));
      await old.promise;
    });

    expect(screen.queryByRole('heading', { name: firstQuestion })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Вопрос другого сезона' })).toBeVisible();
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it('hides old season cards while the next seasons list is still loading', async () => {
    const next = deferred<Response>();
    serve((call) => (call.path === listPath(seasonA) ? json(200, [poll()]) : next.promise));
    const onSignedOut = vi.fn();
    const { rerender } = render(<PollsScreen seasonId={seasonA} onSignedOut={onSignedOut} />);
    await screen.findByRole('heading', { name: firstQuestion });

    rerender(<PollsScreen seasonId={seasonB} onSignedOut={onSignedOut} />);

    expect(screen.queryByRole('heading', { name: firstQuestion })).not.toBeInTheDocument();
    expect(screen.getByTestId('polls-loading')).toHaveAttribute('aria-busy', 'true');
    await act(async () => {
      next.resolve(json(200, [poll({ question: 'Новый сезон' })]));
      await next.promise;
    });
    expect(await screen.findByRole('heading', { name: 'Новый сезон' })).toBeVisible();
  });

  it('does not start another read when an accepted write finishes after unmount', async () => {
    const answer = deferred<Response>();
    const server = serve([poll()], () => answer.promise);
    await refreshCsrf();
    const onSignedOut = vi.fn();
    const { unmount } = render(<PollsScreen seasonId={seasonA} onSignedOut={onSignedOut} />);
    await screen.findByRole('heading', { name: firstQuestion });
    await userEvent.click(radio('Стратегии'));
    await userEvent.click(screen.getByRole('button', { name: 'Голосовать' }));
    await waitFor(() => {
      expect(server.sent('POST', '/vote')).toHaveLength(1);
    });
    unmount();

    await act(async () => {
      answer.resolve(json(200, accepted));
      await answer.promise;
    });

    expect(live.subscriptions.every((subscription) => subscription.stopped)).toBe(true);
    expect(server.sent('GET', '/polls')).toHaveLength(1);
    expect(onSignedOut).not.toHaveBeenCalled();
  });

  it('coalesces a burst during an unfinished read into at most one following read', async () => {
    vi.useFakeTimers();
    const initial = deferred<Response>();
    const following = deferred<Response>();
    let reads = 0;
    const server = serve(() => {
      reads++;
      return reads === 1 ? initial.promise : following.promise;
    });
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(server.sent('GET', '/polls')).toHaveLength(1);
    for (let i = 0; i < 10; i++) push(['vote-cast']);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(server.sent('GET', '/polls')).toHaveLength(1);

    await act(async () => {
      initial.resolve(json(200, [poll()]));
      await initial.promise;
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(server.sent('GET', '/polls')).toHaveLength(2);
    await act(async () => {
      following.resolve(json(200, [poll({ question: 'Свежий снимок' })]));
      await following.promise;
      await vi.advanceTimersByTimeAsync(60_000);
    });

    expect(screen.getByRole('heading', { name: 'Свежий снимок' })).toBeVisible();
    expect(server.sent('GET', '/polls')).toHaveLength(2);
    expect(server.sent('POST', '/vote')).toHaveLength(0);
  });

  it('does not repeatedly poll the backend while the hub is connected and nothing changes', async () => {
    vi.useFakeTimers();
    const server = serve([poll()]);
    render(<PollsScreen seasonId={seasonA} onSignedOut={vi.fn()} />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(screen.getByRole('heading', { name: firstQuestion })).toBeVisible();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120_000);
    });

    expect(server.sent('GET', '/polls')).toHaveLength(1);
  });
});
