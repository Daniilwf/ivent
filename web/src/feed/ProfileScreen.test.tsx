import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { ProfileScreen } from './ProfileScreen';

// H5: a player's profile (SPEC «Профиль», «Отзыв … виден в ленте, профиле и на странице игры», D-124) — name,
// avatar, completed games, seasons with points and place, reviews; every state.

const userId = 'aaaaaaaa-0000-0000-0000-000000000001';
const gameId = '33333333-3333-3333-3333-333333333333';
const seasonId = '99999999-9999-9999-9999-999999999999';

const profile: Schemas['ProfileView'] = {
  id: userId,
  name: 'Капитан_Пельмень',
  avatar: { id: 'f', url: '/api/files/f', thumbnailUrl: '/api/files/f/thumbnail' },
  completed: 5,
  seasons: [
    {
      seasonId,
      seasonName: 'Осень',
      status: 'finished',
      playerId: 'p1',
      points: 42,
      place: 2,
      token: 6,
    },
    {
      seasonId: 's2',
      seasonName: 'Зима',
      status: 'active',
      playerId: 'p2',
      points: 7,
      place: null,
      token: 3,
    },
  ],
  reviews: [
    {
      runId: 'r1',
      gameId,
      gameTitle: 'Hollow Knight',
      seasonId,
      seasonName: 'Осень',
      rating: 9,
      text: 'Лучшая метроидвания',
      completedAt: '2026-09-12T10:00:00Z',
    },
  ],
};

function respond(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function serve(answer: () => Response) {
  vi.stubGlobal(
    'fetch',
    vi.fn((request: Request) =>
      Promise.resolve(
        new URL(request.url).pathname === `/api/users/${userId}` ? answer() : respond(404),
      ),
    ),
  );
}

describe('ProfileScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    globalThis.history.replaceState(null, '', '/');
  });

  it('shows the name, the whole avatar, the completed games, the seasons and the reviews', async () => {
    serve(() => respond(200, profile));

    render(<ProfileScreen userId={userId} meId="someone-else" onSignedOut={vi.fn()} />);

    expect(screen.getByTestId('profile-loading')).toHaveAttribute('aria-busy', 'true');
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Капитан_Пельмень');
    expect(screen.queryByText(ru.profile.you)).toBeNull();
    // The sticker has the colour of the token in the latest season (the first listed), as on its map
    expect(screen.getByTestId('profile').querySelector('span')).toHaveStyle({
      background: 'var(--color-token-7)',
    });
    expect(screen.getByTestId('profile').querySelector('img')).toHaveAttribute(
      'src',
      '/api/files/f',
    );
    expect(screen.getByTestId('profile-completed')).toHaveTextContent('Пройдено 5 игр');

    const seasons = within(screen.getByTestId('profile-seasons')).getAllByRole('listitem');
    expect(seasons[0]).toHaveTextContent('Осень');
    expect(seasons[0]).toHaveTextContent('42 очка');
    expect(seasons[0]).toHaveTextContent('2 место');
    expect(seasons[1]).toHaveTextContent(ru.profile.noPlace);
    expect(within(seasons[0] as HTMLElement).getByRole('link', { name: 'Осень' })).toHaveAttribute(
      'href',
      `/seasons/${seasonId}/feed`,
    );

    const review = within(screen.getByTestId('profile-reviews')).getAllByRole(
      'listitem',
    )[0] as HTMLElement;
    expect(within(review).getByRole('link', { name: 'Hollow Knight' })).toHaveAttribute(
      'href',
      `/games/${gameId}`,
    );
    expect(within(review).getByRole('img', { name: ru.feed.ratingLabel(9) })).toBeInTheDocument();
    expect(review).toHaveTextContent('Лучшая метроидвания');
    expect(review).toHaveTextContent('Осень, 12.09');
  });

  it('marks my own page and calls me to write the first review', async () => {
    serve(() => respond(200, { ...profile, reviews: [], seasons: [], completed: 1 }));

    render(<ProfileScreen userId={userId} meId={userId} onSignedOut={vi.fn()} />);

    expect(await screen.findByText(ru.profile.you)).toBeInTheDocument();
    expect(screen.getByTestId('profile-completed')).toHaveTextContent('Пройдена 1 игра');
    expect(screen.getByRole('heading', { name: ru.profile.noSeasonsTitle })).toBeInTheDocument();
    expect(screen.getByText(ru.profile.noReviewsMine)).toBeInTheDocument();
  });

  it('says a deleted or unknown player has no page and leads back to the season', async () => {
    serve(() => respond(404));
    render(<ProfileScreen userId={userId} meId="me" onSignedOut={vi.fn()} />);

    expect(
      await screen.findByRole('heading', { name: ru.profile.notFoundTitle }),
    ).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: ru.profile.toSeason }));
    expect(globalThis.location.pathname).toBe('/');
  });

  it('says what broke and loads again on retry', async () => {
    let fail = true;
    serve(() => (fail ? respond(500) : respond(200, profile)));
    render(<ProfileScreen userId={userId} meId="me" onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.profile.errorTitle);
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Капитан_Пельмень');
  });

  it('hands a lost session to the sign-in', async () => {
    serve(() => respond(401));
    const onSignedOut = vi.fn();
    render(<ProfileScreen userId={userId} meId="me" onSignedOut={onSignedOut} />);

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalled();
    });
  });
});
