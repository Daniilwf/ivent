import { useState, type ReactNode, type SyntheticEvent } from 'react';
import { api, refreshCsrf, rejectionCode, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Field } from '../ui/Field';
import { Notice } from '../ui/States';

// The engine's rules (AccountRules): at least 8 characters, up to 256.
const minPassword = 8;
const maxPassword = 256;

/**
 * The first thing after signing in with a temporary password (A1, D-106): nothing else opens before the change.
 * The new password is typed twice; the server checks the rest (not the login, not the current one).
 */
export function ChangePasswordForm({
  onChanged,
  temporary = true,
  children,
}: {
  onChanged: (user: Schemas['CurrentUser']) => void;
  /** Signed in with a temporary password (nothing else opens), or changing my own from the menu */
  temporary?: boolean;
  /** Under the main button: a way out (sign out, or back to the game) */
  children?: ReactNode;
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
    <form
      data-private
      onSubmit={(e) => void submit(e)}
      aria-labelledby="password-title"
      className="grid gap-4 rounded-lg border-3 border-ink bg-card p-5 shadow-lift"
    >
      <div className="grid gap-1">
        <h1 id="password-title" className="font-display text-2xl font-heavy">
          {ru.password.title}
        </h1>
        <p className="text-ink-soft">{temporary ? ru.password.why : ru.password.whyOwn}</p>
      </div>
      <Field
        label={temporary ? ru.password.current : ru.password.currentOwn}
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
      <Field
        label={ru.password.next}
        hint={ru.password.rule(minPassword)}
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
      <Field
        label={ru.password.repeat}
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
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <Button data-testid="password-submit" type="submit" variant="main" loading={pending}>
        {ru.password.submit}
      </Button>
      {children}
    </form>
  );
}
