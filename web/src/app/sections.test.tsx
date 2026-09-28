import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { ru } from '../i18n/ru';
import { demoRules } from '../rules/demoRules';
import { fakeServer, json } from '../test/fakeServer';

// H5–H7: the sections of the site — the season, the feed, the pool, the rules — each at its own address, from the
// header (one router, D-202)

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => undefined,
  watchPool: () => () => undefined,
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';

function serve(role: 'player' | 'spectator' = 'player', season: string | null = seasonId) {
  fakeServer({
    'GET /api/auth/me': {
      id: 'u1',
      login: 'vasya',
      name: 'Вася',
      role,
      mustChangePassword: false,
      avatar: null,
    },
    'GET /api/seasons/current': () => (season ? json(200, { id: season }) : json(404)),
    'GET /api/pool': [],
    'GET /api/pool/categories': [],
    [`GET /api/seasons/${seasonId}/games`]: [],
    [`GET /api/seasons/${seasonId}/rules`]: demoRules,
  });
}

describe('the sections in the header', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    window.history.replaceState(null, '', '/');
  });

  it('opens the page of the address and marks its link', async () => {
    window.history.replaceState(null, '', '/pool');
    serve();
    render(<App />);

    expect(
      await screen.findByRole('heading', { level: 1, name: ru.pool.title }),
    ).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: ru.nav.label });
    expect(within(nav).getByRole('link', { name: ru.nav.pool })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(within(nav).getByRole('link', { name: ru.nav.season })).not.toHaveAttribute(
      'aria-current',
    );
  });

  it('goes between sections without a reload, the heading takes the focus, back returns', async () => {
    window.history.replaceState(null, '', '/pool');
    serve();
    render(<App />);
    await screen.findByRole('heading', { level: 1, name: ru.pool.title });

    await userEvent.click(screen.getByRole('link', { name: ru.nav.rules }));

    const rules = await screen.findByRole('heading', { level: 1, name: ru.rules.title });
    expect(window.location.pathname).toBe('/rules');
    // The focus comes on the next frame (the router's usePageHeading)
    await waitFor(() => {
      expect(rules).toHaveFocus();
    });
    expect(await screen.findByText(ru.rules.version(3))).toBeInTheDocument();

    act(() => {
      window.history.back();
    });
    expect(
      await screen.findByRole('heading', { level: 1, name: ru.pool.title }),
    ).toBeInTheDocument();
  });

  it('a spectator reads the pool but cannot add to it', async () => {
    window.history.replaceState(null, '', '/pool');
    serve('spectator');
    render(<App />);

    expect(await screen.findByRole('heading', { name: ru.pool.emptyTitle })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: ru.pool.add })).toBeNull();
  });

  it('without a season the rules say they come with it', async () => {
    window.history.replaceState(null, '', '/rules');
    serve('player', null);
    render(<App />);

    expect(await screen.findByTestId('rules-no-season')).toBeInTheDocument();
  });
});
