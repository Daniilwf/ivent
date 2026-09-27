import { render, screen, waitFor } from '@testing-library/react';
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

  // BUGS.md I1-1: on a slow mobile network each request in a row is a round trip before the season can load
  it('asks who is signed in, the current season and the antiforgery token at once', async () => {
    let answerMe: (response: Response) => void = () => undefined;
    const me = new Promise<Response>((resolve) => {
      answerMe = resolve;
    });
    const server = fakeServer({
      '/api/auth/me': () => me,
      '/api/seasons/current': () => new Response(null, { status: 404 }),
    });

    render(<App />);

    // The season is asked for while the answer about the user is still on its way
    await waitFor(() => {
      expect(server.sent('GET', '/api/seasons/current')).toHaveLength(1);
    });
    expect(server.sent('GET', '/api/auth/antiforgery')).toHaveLength(1);
    answerMe(json(200, user));
    expect(await screen.findByTestId('no-season')).toHaveTextContent(ru.shell.noSeasonTitle);
    expect(server.sent('GET', '/api/seasons/current')).toHaveLength(1);
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
