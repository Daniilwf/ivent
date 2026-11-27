import { act, render, screen, waitFor, within } from '@testing-library/react';
import { standaloneLink } from '../ui/buttonStyles';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import type { SeasonUpdate } from '../api/realtime';
import { ru } from '../i18n/ru';
import { GameScreen } from './GameScreen';
import { fakeServer, json } from '../test/fakeServer';

// H5: a game's page (SPEC «Страница игры: все прохождения, дропы и отзывы», D-124) — the pool's card and every run of
// the game in every season with its review; every state.

const gameId = '33333333-3333-3333-3333-333333333333';
const vasya = 'aaaaaaaa-0000-0000-0000-000000000001';
const reactionPath = `/api/pool/${gameId}/runs/r1/review-reactions`;

const { watchedSeasons } = vi.hoisted(() => ({
  watchedSeasons: new Map<string, (updates: SeasonUpdate[]) => void>(),
}));

vi.mock('../api/realtime', () => ({
  watchSeason: (seasonId: string, update: (updates: SeasonUpdate[]) => void) => {
    watchedSeasons.set(seasonId, update);
    return () => {
      watchedSeasons.delete(seasonId);
    };
  },
}));

const card: Schemas['PoolGameView'] = {
  id: gameId,
  title: 'Hollow Knight',
  tags: ['Метроидвании', 'Инди'],
  hours: 27,
  year: 2017,
  steamAppId: null,
  cover: null,
  note: null,
  isCoop: false,
  author: null,
  isDeleted: false,
  completionCondition: 'Любая концовка',
  deletionReason: null,
};

const run = (over: Partial<Schemas['GameRunView']>): Schemas['GameRunView'] => ({
  runId: 'r1',
  seasonId: 's1',
  seasonName: 'Осень',
  playerId: 'p1',
  userId: vasya,
  playerName: 'Вася',
  status: 'completed',
  difficulty: 'hard',
  hours: 31.5,
  completedAt: '2026-09-12T10:00:00Z',
  rating: 9,
  reviewText: 'Лучшая метроидвания',
  token: 4,
  ...over,
});

function serve(answers: { card: () => Response; runs: () => Response }) {
  fakeServer({
    [`GET /api/pool/${gameId}`]: answers.card,
    [`GET /api/pool/${gameId}/runs`]: answers.runs,
  });
}

describe('GameScreen', () => {
  afterEach(() => {
    watchedSeasons.clear();
    vi.unstubAllGlobals();
    globalThis.history.replaceState(null, '', '/');
  });

  it('shows the card and every run with its status, player link and review', async () => {
    serve({
      card: () => json(200, card),
      runs: () =>
        json(200, [
          run({}),
          run({
            runId: 'r2',
            playerName: 'Петя',
            userId: 'u-petya',
            status: 'dropped',
            rating: null,
            reviewText: null,
            difficulty: null,
            hours: null,
            completedAt: null,
          }),
        ]),
    });

    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    expect(screen.getByTestId('game-loading')).toHaveAttribute('aria-busy', 'true');
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Hollow Knight');
    const header = screen.getByTestId('game');
    expect(header).toHaveTextContent(ru.hours.estimate(27));
    expect(header).toHaveTextContent('2017 г.');
    expect(within(header).getByRole('list', { name: ru.gamePage.tags })).toHaveTextContent(
      'Метроидвании',
    );
    expect(screen.getByText(ru.gamePage.condition('Любая концовка'))).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: ru.gamePage.runsTitle(2) })).toBeInTheDocument();

    const [done, dropped] = [...screen.getByTestId('game-runs').children] as HTMLElement[];
    // The player's sticker has their token colour in that season. The inline declaration is read:
    // jsdom 30.1.3 substitutes var() in computed styles, and the test page defines no tokens (D-251)
    expect(done?.querySelector('span')?.style.background).toBe('var(--color-token-5)');
    const player = within(done as HTMLElement).getByRole('link', { name: 'Вася' });
    expect(player).toHaveAttribute('href', `/users/${vasya}`);
    // A link that stands on its own is a 44 px tap target on a phone (DESIGN.md), as every standalone link
    expect(player.className).toContain(standaloneLink);
    expect(done).toHaveTextContent(ru.gamePage.status['completed'] ?? '');
    expect(done).toHaveTextContent(ru.difficulty.hard);
    // Glossary «Часы»: the game's length, not a time played nobody measured (K1A)
    expect(done).toHaveTextContent('длина 31,5 ч');
    expect(done).not.toHaveTextContent('в игре');
    expect(done).toHaveTextContent('Лучшая метроидвания');
    expect(
      within(done as HTMLElement).getByRole('img', { name: ru.feed.ratingLabel(9) }),
    ).toBeInTheDocument();
    expect(dropped).toHaveTextContent(ru.gamePage.status['dropped'] ?? '');
    expect(dropped?.querySelector('blockquote')).toBeNull();
    expect(done?.querySelector('blockquote')).not.toBeNull();
  });

  it('says nobody took the game yet', async () => {
    serve({ card: () => json(200, { ...card, isDeleted: true }), runs: () => json(200, []) });

    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    expect(
      await screen.findByRole('heading', { name: ru.gamePage.noRunsTitle }),
    ).toBeInTheDocument();
    expect(screen.getByTestId('game')).toHaveTextContent(ru.gamePage.deleted);
  });

  it('opens only the enabled review comment thread and shows moderation granted by the server', async () => {
    const path = `/api/pool/${gameId}/runs/r1/review-comments`;
    const commentText = 'Даже <b>здесь</b> это обычный текст.';
    const server = fakeServer({
      [`GET /api/pool/${gameId}`]: card,
      [`GET /api/pool/${gameId}/runs`]: [
        { ...run({}), canComment: true },
        run({ runId: 'r2', seasonId: 's2', reviewText: 'Старый отзыв без обсуждения' }),
        run({ runId: 'r3', status: 'dropped', rating: null, reviewText: null }),
      ],
      [`GET ${path}`]: {
        comments: [
          {
            id: '1c000000-0000-0000-0000-000000000091',
            author: 'Петя',
            text: commentText,
            createdAt: '2026-09-12T10:00:00Z',
            editedAt: null,
            canEdit: false,
            canHide: true,
          },
        ],
        nextBefore: null,
      },
    });
    const user = userEvent.setup();
    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    expect(await screen.findByText('Старый отзыв без обсуждения')).toBeVisible();
    expect(server.sent('GET', '/review-comments')).toHaveLength(0);
    const disclosures = screen.queryAllByRole('button', { name: /^Комментарии/ });
    expect(disclosures).toHaveLength(1);
    disclosures[0]?.focus();
    await user.keyboard('{Enter}');

    expect(await screen.findByText(commentText)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Скрыть' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Изменить' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Удалить' })).toBeNull();
    expect(server.sent('GET', path)).toHaveLength(1);
  });

  it('shows the configured reactions and changes only the chosen reaction from the keyboard', async () => {
    let fireSelected = false;
    const server = fakeServer({
      [`GET /api/pool/${gameId}`]: card,
      [`GET /api/pool/${gameId}/runs`]: () =>
        json(200, [
          {
            ...run({}),
            reviewReactions: [
              { emoji: '🔥', count: fireSelected ? 2 : 1, selected: fireSelected },
              { emoji: '❤️', count: 1, selected: false },
            ],
          },
          run({ runId: 'r2', reviewText: 'Старый отзыв без реакций' }),
        ]),
      [`PUT ${reactionPath}`]: () => {
        fireSelected = !fireSelected;
        return json(204);
      },
    });
    const user = userEvent.setup();
    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    const initial = await screen.findAllByRole('button', { pressed: false });
    expect(initial).toHaveLength(2);
    const fire = initial.find((button) => button.textContent.includes('🔥'));
    const heart = initial.find((button) => button.textContent.includes('❤️'));
    if (!fire || !heart) throw new Error('The configured emoji must both be in the buttons');
    expect(fire).toHaveAccessibleName();
    expect(heart).toHaveAccessibleName();
    expect(fire).toHaveTextContent('1');

    fire.focus();
    await user.keyboard('{Enter}');

    const selected = await screen.findByRole('button', { pressed: true });
    expect(selected).toHaveTextContent('🔥');
    expect(selected).toHaveTextContent('2');
    expect(selected).toHaveFocus();
    expect(heart).toHaveAttribute('aria-pressed', 'false');
    await user.keyboard(' ');
    await waitFor(() => {
      expect(screen.queryByRole('button', { pressed: true })).toBeNull();
      expect(server.sent('PUT', reactionPath)).toHaveLength(2);
    });

    const commandId: unknown = expect.any(String);
    expect(server.sent('PUT', reactionPath).map((call) => call.body)).toEqual([
      { commandId, emoji: '🔥', selected: true },
      { commandId, emoji: '🔥', selected: false },
    ]);
  });

  it('keeps a review and its previous reaction when the write fails', async () => {
    fakeServer({
      [`GET /api/pool/${gameId}`]: card,
      [`GET /api/pool/${gameId}/runs`]: [
        { ...run({}), reviewReactions: [{ emoji: '🔥', count: 2, selected: true }] },
      ],
      [`PUT ${reactionPath}`]: () => json(409, { detail: 'Реакции недоступны' }),
    });
    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { pressed: true }));

    expect(await screen.findByRole('alert')).toBeVisible();
    expect(screen.getByText('Лучшая метроидвания')).toBeVisible();
    expect(screen.getByRole('button', { pressed: true })).toHaveTextContent('2');
  });

  it('refreshes enabled reviews after a reaction from another session and keeps focus', async () => {
    let count = 1;
    const server = fakeServer({
      [`GET /api/pool/${gameId}`]: card,
      [`GET /api/pool/${gameId}/runs`]: () =>
        json(200, [
          run({ reviewReactions: [{ emoji: '🔥', count, selected: false }] }),
          run({ runId: 'r2', seasonId: 's2' }),
        ]),
    });
    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    const button = await screen.findByRole('button', { name: 'Реакция 🔥', pressed: false });
    expect(button).toHaveAccessibleDescription('1 реакция');
    expect([...watchedSeasons.keys()]).toEqual(['s1']);
    button.focus();

    count = 2;
    watchedSeasons.get('s1')?.([
      { seasonId: 's1', fromSequence: 10, toSequence: 10, types: ['review-reaction-set'] },
    ]);

    await waitFor(() => {
      expect(button).toHaveAccessibleDescription('2 реакции');
    });
    expect(button).toHaveAttribute('aria-pressed', 'false');
    expect(button).toHaveFocus();
    expect(server.sent('GET', `/api/pool/${gameId}`)).toHaveLength(1);
    expect(server.sent('PUT', reactionPath)).toHaveLength(0);
  });

  it('keeps a saved reaction when an overlapping live refresh fails, then adopts the next snapshot', async () => {
    let finishRead!: (response: Response) => void;
    const delayedRead = new Promise<Response>((resolve) => {
      finishRead = resolve;
    });
    let written = false;
    let readsSinceWrite = 0;
    let recovered = false;
    const snapshot = (count: number, selected: boolean) =>
      json(200, [run({ reviewReactions: [{ emoji: '🔥', count, selected }] })]);
    fakeServer({
      [`GET /api/pool/${gameId}`]: card,
      [`GET /api/pool/${gameId}/runs`]: () => {
        if (recovered) return snapshot(5, false);
        if (!written) return snapshot(1, false);
        if (++readsSinceWrite === 1) return delayedRead;
        return json(500);
      },
      [`PUT ${reactionPath}`]: () => {
        written = true;
        return json(204);
      },
    });
    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    const button = await screen.findByRole('button', { name: 'Реакция 🔥', pressed: false });
    await userEvent.click(button);
    await waitFor(() => {
      expect(readsSinceWrite).toBe(1);
    });

    const refreshFromHub = async (sequence: number) => {
      vi.useFakeTimers();
      try {
        await act(async () => {
          watchedSeasons.get('s1')?.([
            {
              seasonId: 's1',
              fromSequence: sequence,
              toSequence: sequence,
              types: ['review-reaction-set'],
            },
          ]);
          await vi.runOnlyPendingTimersAsync();
        });
      } finally {
        vi.useRealTimers();
      }
    };

    await refreshFromHub(10);
    expect(readsSinceWrite).toBe(2);
    await act(async () => {
      finishRead(snapshot(2, true));
      await delayedRead;
    });

    expect(button).toHaveAttribute('aria-pressed', 'true');
    expect(button).toHaveAccessibleDescription('2 реакции');
    expect(button).toHaveFocus();
    expect(await screen.findByText(ru.gamePage.reviewReactions.stale)).toBeVisible();

    recovered = true;
    await refreshFromHub(11);
    expect(button).toHaveAttribute('aria-pressed', 'false');
    expect(button).toHaveAccessibleDescription('5 реакций');
    expect(button).toHaveFocus();
    expect(screen.queryByText(ru.gamePage.reviewReactions.stale)).toBeNull();
  });

  it('says an unknown or deleted game has no page', async () => {
    serve({ card: () => json(404), runs: () => json(404) });

    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    expect(
      await screen.findByRole('heading', { name: ru.gamePage.notFoundTitle }),
    ).toBeInTheDocument();
  });

  it('says what broke and loads again on retry', async () => {
    let fail = true;
    serve({ card: () => json(200, card), runs: () => (fail ? json(500) : json(200, [])) });
    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.gamePage.errorTitle);
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Hollow Knight');
  });

  it('hands a lost session to the sign-in', async () => {
    serve({ card: () => json(401), runs: () => json(401) });
    const onSignedOut = vi.fn();
    render(<GameScreen gameId={gameId} onSignedOut={onSignedOut} />);

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalled();
    });
  });
});
