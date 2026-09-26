import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { navigate } from '../app/router';
import { ru } from '../i18n/ru';

// H5: the feed, the profile and the game page as pages of the site — opened by address, from the header's sections and
// from my menu (D-150).

vi.mock('../api/realtime', () => ({ watchSeason: () => () => undefined }));

const seasonId = '99999999-9999-9999-9999-999999999999';
const userId = 'aaaaaaaa-0000-0000-0000-000000000001';
const user = {
  id: userId,
  login: 'vasya',
  name: 'Вася',
  role: 'player',
  mustChangePassword: false,
  avatar: null,
};
const emptyFeed = { entries: [], nextBefore: null, players: [], games: [], runs: [] };
const profile = { id: userId, name: 'Вася', avatar: null, seasons: [], reviews: [], completed: 0 };

function respond(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function serve(routes: Record<string, () => Response>) {
  vi.stubGlobal(
    'fetch',
    vi.fn((request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === '/api/auth/antiforgery')
        return Promise.resolve(respond(200, { token: 't', headerName: 'X-CSRF-TOKEN' }));
      return Promise.resolve(routes[path]?.() ?? respond(404));
    }),
  );
}

const site = {
  '/api/auth/me': () => respond(200, user),
  '/api/seasons/current': () => respond(200, { id: seasonId }),
  [`/api/seasons/${seasonId}/feed`]: () => respond(200, emptyFeed),
  [`/api/users/${userId}`]: () => respond(200, profile),
};

describe('the pages of the site', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    globalThis.history.replaceState(null, '', '/');
  });

  it('opens the current season’s feed by its address and marks its section in the header', async () => {
    globalThis.history.replaceState(null, '', '/feed');
    serve(site);

    render(<App />);

    expect(
      await screen.findByRole('heading', { level: 1, name: ru.feed.title }),
    ).toBeInTheDocument();
    // One row of sections for a phone and a desktop (D-202)
    const nav = screen.getByRole('navigation', { name: ru.nav.label });
    expect(within(nav).getByRole('link', { name: ru.nav.feed })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(within(nav).getByRole('link', { name: ru.nav.season })).not.toHaveAttribute(
      'aria-current',
    );
  });

  it('goes to the feed from the header’s sections without a reload', async () => {
    serve(site);
    render(<App />);
    const nav = await screen.findByRole('navigation', { name: ru.nav.label });

    await userEvent.click(within(nav).getByRole('link', { name: ru.nav.feed }));

    expect(globalThis.location.pathname).toBe('/feed');
    expect(
      await screen.findByRole('heading', { level: 1, name: ru.feed.title }),
    ).toBeInTheDocument();
  });

  it('opens my profile from my menu', async () => {
    serve(site);
    const person = userEvent.setup();
    render(<App />);

    await person.click(await screen.findByTestId('user-menu'));
    await person.click(await screen.findByTestId('my-profile'));

    expect(globalThis.location.pathname).toBe(`/users/${userId}`);
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Вася');
    expect(screen.getByText(ru.profile.you)).toBeInTheDocument();
    expect(screen.getByTestId('profile-completed')).toHaveTextContent(ru.profile.completed(0));
    expect(ru.profile.completed(0)).toBe('Пока без пройденных игр');
  });

  it('leaves my password form for the page a link opens', async () => {
    serve(site);
    const person = userEvent.setup();
    render(<App />);
    await person.click(await screen.findByTestId('user-menu'));
    await person.click(await screen.findByTestId('change-password'));
    expect(screen.getByRole('heading', { level: 1, name: ru.password.title })).toBeInTheDocument();

    act(() => {
      navigate('/feed');
    });

    expect(
      await screen.findByRole('heading', { level: 1, name: ru.feed.title }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: ru.password.title })).toBeNull();
  });

  it('says a page does not exist and leads back to the season', async () => {
    globalThis.history.replaceState(null, '', '/nowhere');
    serve(site);

    render(<App />);

    expect(await screen.findByTestId('page-not-found')).toHaveTextContent(
      ru.feed.pageNotFoundTitle,
    );
  });
});
