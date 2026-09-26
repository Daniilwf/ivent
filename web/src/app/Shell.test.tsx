import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { registerConnection } from '../api/connection';
import { ru } from '../i18n/ru';

// H1: the shell of a signed-in page — the connection mark, my menu, a password change of my own.

vi.mock('../api/realtime', () => ({ watchSeason: () => () => undefined }));

function respond(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const user = {
  id: 'u1',
  login: 'vasya',
  name: 'Вася',
  role: 'player',
  mustChangePassword: false,
  avatar: null,
};

function serve(routes: Record<string, (request: Request) => Response>) {
  const seen: { path: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      seen.push({
        path,
        body:
          request.method === 'POST'
            ? await request
                .clone()
                .json()
                .catch(() => null)
            : null,
      });
      if (path === '/api/auth/antiforgery')
        return respond(200, { token: 't', headerName: 'X-CSRF-TOKEN' });
      return routes[path]?.(request) ?? new Response(null, { status: 404 });
    }),
  );
  return seen;
}

describe('the shell', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows «no connection» while a live subscription is offline, and hides it when it is back', async () => {
    serve({ '/api/auth/me': () => respond(200, user) });
    render(<App />);
    await screen.findByTestId('no-season');
    expect(screen.queryByText(ru.ui.connectionLost)).toBeNull();

    const voice = registerConnection();
    act(() => {
      voice.report('offline');
    });
    expect(screen.getByText(ru.ui.connectionLost)).toBeInTheDocument();

    act(() => {
      voice.report('online');
    });
    expect(screen.queryByText(ru.ui.connectionLost)).toBeNull();
    voice.release();
  });

  it('changes my own password from the menu and keeps me in the game', async () => {
    const renamed = { ...user, name: 'Вася' };
    const seen = serve({
      '/api/auth/me': () => respond(200, renamed),
      '/api/auth/password': () => new Response(null, { status: 204 }),
    });
    const person = userEvent.setup();
    render(<App />);
    await screen.findByTestId('no-season');

    await person.click(screen.getByTestId('user-menu'));
    await person.click(await screen.findByTestId('change-password'));

    // My own password: the current one, not a temporary one; the page's title takes the focus
    expect(screen.getByLabelText(ru.password.currentOwn)).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByRole('heading', { level: 1, name: ru.password.title })).toHaveFocus();
    });

    await person.type(screen.getByTestId('password-current'), 'old-password');
    await person.type(screen.getByTestId('password-new'), 'new-password');
    await person.type(screen.getByTestId('password-repeat'), 'new-password');
    await person.click(screen.getByTestId('password-submit'));

    expect(await screen.findByTestId('no-season')).toBeInTheDocument();
    expect(seen.some((r) => r.path === '/api/auth/password')).toBe(true);
    expect(screen.queryByTestId('login-submit')).toBeNull();
  });

  it('goes back to the game from my password form without changing anything', async () => {
    const seen = serve({ '/api/auth/me': () => respond(200, user) });
    const person = userEvent.setup();
    render(<App />);
    await screen.findByTestId('no-season');

    await person.click(screen.getByTestId('user-menu'));
    await person.click(await screen.findByTestId('change-password'));
    await person.click(screen.getByRole('button', { name: ru.password.back }));

    expect(await screen.findByTestId('no-season')).toBeInTheDocument();
    expect(seen.some((r) => r.path === '/api/auth/password')).toBe(false);
  });

  it('says a too short new password at its field', async () => {
    serve({ '/api/auth/me': () => respond(200, user) });
    const person = userEvent.setup();
    render(<App />);
    await screen.findByTestId('no-season');
    await person.click(screen.getByTestId('user-menu'));
    await person.click(await screen.findByTestId('change-password'));
    // The form takes the focus on the next frame; typing starts after it, as a person's would
    await waitFor(() => {
      expect(screen.getByRole('heading', { level: 1, name: ru.password.title })).toHaveFocus();
    });

    await person.type(screen.getByTestId('password-current'), 'old-password');
    await person.type(screen.getByTestId('password-new'), 'short');
    await person.type(screen.getByTestId('password-repeat'), 'short');
    await person.click(screen.getByTestId('password-submit'));

    const field = screen.getByTestId('password-new');
    expect(field).toHaveAttribute('aria-invalid', 'true');
    expect(field).toHaveAccessibleDescription(`${ru.password.rule(8)} ${ru.password.tooShort(8)}`);
  });
});
