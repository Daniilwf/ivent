import { render, screen } from '@testing-library/react';
import { App } from './App';
import { ru } from './i18n/ru';

vi.mock('./api/realtime', () => ({ watchSeason: () => () => undefined }));

function respond(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
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
      return Promise.resolve(routes[path]?.() ?? new Response(null, { status: 404 }));
    }),
  );
}

const user = { id: 'u1', login: 'vasya', name: 'Вася', role: 'player', mustChangePassword: false };

describe('App', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the sign-in form to a visitor without a session', async () => {
    serve({ '/api/auth/me': () => new Response(null, { status: 401 }) });

    render(<App />);

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent(ru.login.title);
    expect(screen.getByTestId('login-submit')).toBeEnabled();
  });

  it('says there are no seasons when the site has none', async () => {
    serve({ '/api/auth/me': () => respond(200, user) });

    render(<App />);

    expect(await screen.findByTestId('no-season')).toHaveTextContent(ru.app.noSeason);
  });

  it('reports a server failure instead of pretending there is no season', async () => {
    serve({
      '/api/auth/me': () => respond(200, user),
      '/api/seasons/current': () => new Response(null, { status: 500 }),
    });

    render(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.app.loadError);
  });
});
