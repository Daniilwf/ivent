import { useState, type ReactNode, type SyntheticEvent } from 'react';
import { api, refreshCsrf, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Field } from '../ui/Field';
import { Notice } from '../ui/States';
import { Sticker } from '../ui/Sticker';

/** A few meeples on the table above the form: the game's face before anything is known about the player */
function Meeples() {
  return (
    <div className="flex justify-center -space-x-2" aria-hidden>
      {Array.from(ru.shell.meeples).map((letter, i) => (
        <span key={i} className={i % 2 ? 'rotate-6' : '-rotate-6'}>
          <Sticker player={{ name: letter, token: [0, 4, 2, 6, 3][i] ?? 0 }} size={48} />
        </span>
      ))}
    </div>
  );
}

/** The wooden table with the sign-in card on it: login and password changes both stand here */
export function TablePage({ children }: { children: ReactNode }) {
  return (
    <main className="table-surface grid min-h-dvh place-items-center px-4 py-8">
      <div className="grid w-full max-w-100 gap-4">
        <Meeples />
        {children}
      </div>
    </main>
  );
}

export function LoginForm({ onSignedIn }: { onSignedIn: (user: Schemas['CurrentUser']) => void }) {
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function submit(event: SyntheticEvent) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      await refreshCsrf();
      const { data, response } = await api.POST('/api/auth/login', { body: { login, password } });
      if (data) {
        await refreshCsrf();
        onSignedIn(data);
        return;
      }
      setError(
        response.status === 429
          ? ru.login.throttled
          : response.status === 401
            ? ru.login.failed
            : ru.app.loadError,
      );
    } catch {
      setError(ru.app.loadError);
    } finally {
      setPending(false);
    }
  }

  return (
    <TablePage>
      <form
        data-private
        onSubmit={(e) => void submit(e)}
        aria-labelledby="login-title"
        className="grid gap-4 rounded-lg border-3 border-ink bg-card p-5 shadow-lift"
      >
        <div className="grid gap-1">
          <h1 id="login-title" className="font-display text-2xl font-heavy">
            {ru.login.title}
          </h1>
          <p className="text-ink-soft">{ru.shell.loginLead}</p>
        </div>
        <Field
          label={ru.login.login}
          data-testid="login-name"
          value={login}
          onChange={(e) => {
            setLogin(e.target.value);
          }}
          autoComplete="username"
          required
        />
        <Field
          label={ru.login.password}
          data-testid="login-password"
          type="password"
          value={password}
          onChange={(e) => {
            setPassword(e.target.value);
          }}
          autoComplete="current-password"
          required
        />
        {error ? <Notice tone="danger">{error}</Notice> : null}
        <Button data-testid="login-submit" type="submit" variant="main" loading={pending}>
          {ru.login.submit}
        </Button>
      </form>
    </TablePage>
  );
}
