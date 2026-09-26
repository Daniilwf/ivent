import { useState, type SyntheticEvent } from 'react';
import { api, refreshCsrf, rejectionCode, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';

// The engine's rules (AccountRules): at least 8 characters, up to 256.
const minPassword = 8;
const maxPassword = 256;

/**
 * The first thing after signing in with a temporary password (A1, D-106): nothing else opens before the change.
 * The new password is typed twice; the server checks the rest (not the login, not the current one).
 */
export function ChangePasswordForm({
  onChanged,
}: {
  onChanged: (user: Schemas['CurrentUser']) => void;
}) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function submit(event: SyntheticEvent) {
    event.preventDefault();
    if (next.length < minPassword) {
      setError(ru.password.tooShort(minPassword));
      return;
    }

    if (next !== repeat) {
      setError(ru.password.mismatch);
      return;
    }

    setPending(true);
    setError(null);
    try {
      const { error: problem } = await api.POST('/api/auth/password', {
        body: { commandId: crypto.randomUUID(), currentPassword: current, newPassword: next },
      });
      if (problem) {
        const code = rejectionCode(problem);
        setError((code && ru.rejection[code]) ?? ru.rejection.unknown);
        return;
      }

      // The change signed this session in again with a new stamp: fresh token, fresh account
      await refreshCsrf();
      const { data } = await api.GET('/api/auth/me');
      if (data) onChanged(data);
      else setError(ru.app.loadError);
    } catch {
      setError(ru.app.loadError);
    } finally {
      setPending(false);
    }
  }

  return (
    <form data-private onSubmit={(e) => void submit(e)} aria-labelledby="password-title">
      <h1 id="password-title">{ru.password.title}</h1>
      <p>{ru.password.why}</p>
      <label>
        {ru.password.current}
        <input
          data-testid="password-current"
          type="password"
          value={current}
          maxLength={maxPassword}
          onChange={(e) => {
            setCurrent(e.target.value);
          }}
          autoComplete="current-password"
          required
        />
      </label>
      <label>
        {ru.password.next}
        <input
          data-testid="password-new"
          type="password"
          value={next}
          maxLength={maxPassword}
          onChange={(e) => {
            setNext(e.target.value);
          }}
          autoComplete="new-password"
          required
        />
      </label>
      <label>
        {ru.password.repeat}
        <input
          data-testid="password-repeat"
          type="password"
          value={repeat}
          maxLength={maxPassword}
          onChange={(e) => {
            setRepeat(e.target.value);
          }}
          autoComplete="new-password"
          required
        />
      </label>
      {error && <p role="alert">{error}</p>}
      <button data-testid="password-submit" type="submit" disabled={pending}>
        {ru.password.submit}
      </button>
    </form>
  );
}
