import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { GameScreen } from './GameScreen';

// H5: a game's page (SPEC «Страница игры: все прохождения, дропы и отзывы», D-124) — the pool's card and every run of
// the game in every season with its review; every state.

const gameId = '33333333-3333-3333-3333-333333333333';
const vasya = 'aaaaaaaa-0000-0000-0000-000000000001';

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

function respond(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function serve(answers: { card: () => Response; runs: () => Response }) {
  vi.stubGlobal(
    'fetch',
    vi.fn((request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === `/api/pool/${gameId}`) return Promise.resolve(answers.card());
      if (path === `/api/pool/${gameId}/runs`) return Promise.resolve(answers.runs());
      return Promise.resolve(respond(404));
    }),
  );
}

describe('GameScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    globalThis.history.replaceState(null, '', '/');
  });

  it('shows the card and every run with its status, player link and review', async () => {
    serve({
      card: () => respond(200, card),
      runs: () =>
        respond(200, [
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
    // The player's sticker has their token colour in that season
    expect(done?.querySelector('span')).toHaveStyle({ background: 'var(--color-token-5)' });
    expect(within(done as HTMLElement).getByRole('link', { name: 'Вася' })).toHaveAttribute(
      'href',
      `/users/${vasya}`,
    );
    expect(done).toHaveTextContent(ru.gamePage.status['completed'] ?? '');
    expect(done).toHaveTextContent(ru.difficulty.hard);
    expect(done).toHaveTextContent('31,5 ч в игре');
    expect(done).toHaveTextContent('Лучшая метроидвания');
    expect(
      within(done as HTMLElement).getByRole('img', { name: ru.feed.ratingLabel(9) }),
    ).toBeInTheDocument();
    expect(dropped).toHaveTextContent(ru.gamePage.status['dropped'] ?? '');
    expect(dropped?.querySelector('blockquote')).toBeNull();
    expect(done?.querySelector('blockquote')).not.toBeNull();
  });

  it('says nobody took the game yet', async () => {
    serve({ card: () => respond(200, { ...card, isDeleted: true }), runs: () => respond(200, []) });

    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    expect(
      await screen.findByRole('heading', { name: ru.gamePage.noRunsTitle }),
    ).toBeInTheDocument();
    expect(screen.getByTestId('game')).toHaveTextContent(ru.gamePage.deleted);
  });

  it('says an unknown or deleted game has no page', async () => {
    serve({ card: () => respond(404), runs: () => respond(404) });

    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    expect(
      await screen.findByRole('heading', { name: ru.gamePage.notFoundTitle }),
    ).toBeInTheDocument();
  });

  it('says what broke and loads again on retry', async () => {
    let fail = true;
    serve({ card: () => respond(200, card), runs: () => (fail ? respond(500) : respond(200, [])) });
    render(<GameScreen gameId={gameId} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.gamePage.errorTitle);
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Hollow Knight');
  });

  it('hands a lost session to the sign-in', async () => {
    serve({ card: () => respond(401), runs: () => respond(401) });
    const onSignedOut = vi.fn();
    render(<GameScreen gameId={gameId} onSignedOut={onSignedOut} />);

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalled();
    });
  });
});
