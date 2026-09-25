import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { ru } from '../i18n/ru';

// After signing in with a temporary password nothing but its change opens (A1, D-106).

vi.mock('../api/realtime', () => ({ watchSeason: () => () => undefined }));

function respond(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const temporaryUser = {
  id: 'u1',
  login: 'lyosha',
  name: 'Лёша',
  role: 'player',
  mustChangePassword: true,
};

/** Serves the session; the password change answers with `answer`; after it, «me» no longer asks for a change. */
function serve(answer: () => Response) {
  let changed = false;
  const sent: unknown[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const path = new URL(request.url).pathname;
      if (path === '/api/auth/antiforgery')
        return respond(200, { token: 't', headerName: 'X-CSRF-TOKEN' });
      if (path === '/api/auth/me')
        return respond(200, { ...temporaryUser, mustChangePassword: !changed });
      if (path === '/api/auth/password') {
        sent.push(await request.json());
        const response = answer();
        changed = response.ok;
        return response;
      }
      if (path === '/api/seasons/current') return new Response(null, { status: 404 });
      return new Response(null, { status: 404 });
    }),
  );
  return sent;
}

async function fill(current: string, next: string, repeat = next) {
  await userEvent.type(await screen.findByTestId('password-current'), current);
  await userEvent.type(screen.getByTestId('password-new'), next);
  await userEvent.type(screen.getByTestId('password-repeat'), repeat);
  await userEvent.click(screen.getByTestId('password-submit'));
}

describe('Password change', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('comes first after a temporary password, instead of the season', async () => {
    serve(() => respond(200, {}));
    render(<App />);

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent(ru.password.title);
    expect(screen.queryByTestId('no-season')).toBeNull();
  });

  it('changes the password and lets the player in', async () => {
    const sent = serve(() =>
      respond(200, { duplicate: false, account: {}, temporaryPassword: null }),
    );
    render(<App />);

    await fill('времянка-1', 'свой-пароль-1');

    expect(await screen.findByTestId('no-season')).toBeInTheDocument();
    expect(sent[0]).toMatchObject({
      currentPassword: 'времянка-1',
      newPassword: 'свой-пароль-1',
    });
  });

  it('refuses a short password before asking the server', async () => {
    const sent = serve(() => respond(200, {}));
    render(<App />);

    await fill('времянка-1', 'short');

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.password.tooShort(8));
    expect(sent).toHaveLength(0);
  });

  it('refuses two different new passwords', async () => {
    const sent = serve(() => respond(200, {}));
    render(<App />);

    await fill('времянка-1', 'свой-пароль-1', 'свой-пароль-2');

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.password.mismatch);
    expect(sent).toHaveLength(0);
  });

  it('says in Russian when the temporary password is wrong', async () => {
    serve(() =>
      respond(409, { title: 'rejected', status: 409, code: 'account.currentPasswordWrong' }),
    );
    render(<App />);

    await fill('не-тот-пароль', 'свой-пароль-1');

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.rejection['account.currentPasswordWrong'],
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(ru.password.title);
  });
});
