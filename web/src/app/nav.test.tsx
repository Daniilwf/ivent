import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { ru } from '../i18n/ru';
import { demoRules } from '../rules/demoRules';
import { pageOf } from './nav';

// H6, H7: the sections of the site — the main page, the pool, the rules — each at its own address, from the header

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => undefined,
  watchPool: () => () => undefined,
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';

function respond(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function serve(role: 'player' | 'spectator' = 'player', season: string | null = seasonId) {
  const routes: Record<string, () => Response> = {
    '/api/auth/antiforgery': () => respond(200, { token: 't', headerName: 'X-CSRF-TOKEN' }),
    '/api/auth/me': () =>
      respond(200, {
        id: 'u1',
        login: 'vasya',
        name: 'Вася',
        role,
        mustChangePassword: false,
        avatar: null,
      }),
    '/api/seasons/current': () =>
      season ? respond(200, { id: season }) : new Response(null, { status: 404 }),
    '/api/pool': () => respond(200, []),
    '/api/pool/categories': () => respond(200, []),
    [`/api/seasons/${seasonId}/games`]: () => respond(200, []),
    [`/api/seasons/${seasonId}/rules`]: () => respond(200, demoRules),
  };
  vi.stubGlobal(
    'fetch',
    vi.fn((request: Request) =>
      Promise.resolve(
        routes[new URL(request.url).pathname]?.() ?? new Response(null, { status: 404 }),
      ),
    ),
  );
}

describe('the address of a section', () => {
  it('names the section, the main page for anything unknown', () => {
    expect(pageOf('/')).toBe('home');
    expect(pageOf('/pool')).toBe('pool');
    expect(pageOf('/pool/')).toBe('pool');
    expect(pageOf('/rules')).toBe('rules');
    expect(pageOf('/whatever')).toBe('home');
  });
});

describe('the sections in the header', () => {
  beforeEach(() => {
    vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
  });

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
    expect(within(nav).getByRole('link', { name: ru.nav.home })).not.toHaveAttribute(
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
    expect(rules).toHaveFocus();
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
