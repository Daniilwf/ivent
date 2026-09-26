import { useState, type SyntheticEvent } from 'react';
import { api, refreshCsrf, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';

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
    <form data-private onSubmit={(e) => void submit(e)} aria-labelledby="login-title">
      <h1 id="login-title">{ru.login.title}</h1>
      <label>
        {ru.login.login}
        <input
          data-testid="login-name"
          value={login}
          onChange={(e) => {
            setLogin(e.target.value);
          }}
          autoComplete="username"
          required
        />
      </label>
      <label>
        {ru.login.password}
        <input
          data-testid="login-password"
          type="password"
          value={password}
          onChange={(e) => {
            setPassword(e.target.value);
          }}
          autoComplete="current-password"
          required
        />
      </label>
      {error && <p role="alert">{error}</p>}
      <button data-testid="login-submit" type="submit" disabled={pending}>
        {ru.login.submit}
      </button>
    </form>
  );
}
