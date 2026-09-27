import { render, screen } from '@testing-library/react';
import { App } from './App';
import { ru } from './i18n/ru';
import { fakeServer, json } from './test/fakeServer';

vi.mock('./api/realtime', () => ({ watchSeason: () => () => undefined }));

function serve(routes: Record<string, () => Response>) {
  fakeServer(routes);
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
    serve({ '/api/auth/me': () => json(200, user) });

    render(<App />);

    expect(await screen.findByTestId('no-season')).toHaveTextContent(ru.shell.noSeasonTitle);
  });

  it('reports a server failure instead of pretending there is no season', async () => {
    serve({
      '/api/auth/me': () => json(200, user),
      '/api/seasons/current': () => new Response(null, { status: 500 }),
    });

    render(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.shell.loadErrorTitle);
  });
});
