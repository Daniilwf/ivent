import { useCallback, useEffect, useState } from 'react';
import { api, refreshCsrf, type Schemas } from './api/client';
import { ChangePasswordForm } from './app/ChangePasswordForm';
import { LoginForm } from './app/LoginForm';
import { ru } from './i18n/ru';
import { SeasonScreen } from './season/SeasonScreen';

type State =
  | { kind: 'loading' }
  | { kind: 'failed' }
  | { kind: 'signedOut' }
  | { kind: 'changePassword' }
  | { kind: 'signedIn'; user: Schemas['CurrentUser']; seasonId: string | null };

export function App() {
  const [state, setState] = useState<State>({ kind: 'loading' });

  const signedOut = useCallback(() => {
    setState({ kind: 'signedOut' });
  }, []);

  const enter = useCallback(async (user: Schemas['CurrentUser']) => {
    // A temporary password opens nothing but its change (D-106)
    if (user.mustChangePassword) {
      setState({ kind: 'changePassword' });
      return;
    }

    const { data, response } = await api.GET('/api/seasons/current');
    if (data) setState({ kind: 'signedIn', user, seasonId: data.id });
    else if (response.status === 404) setState({ kind: 'signedIn', user, seasonId: null });
    else if (response.status === 401) setState({ kind: 'signedOut' });
    else setState({ kind: 'failed' });
  }, []);

  useEffect(() => {
    void (async () => {
      try {
        await refreshCsrf();
        const { data, response } = await api.GET('/api/auth/me');
        if (data) await enter(data);
        else if (response.status === 401) setState({ kind: 'signedOut' });
        else setState({ kind: 'failed' });
      } catch {
        setState({ kind: 'failed' });
      }
    })();
  }, [enter]);

  async function logout() {
    try {
      await api.POST('/api/auth/logout');
      await refreshCsrf();
    } finally {
      setState({ kind: 'signedOut' });
    }
  }

  if (state.kind === 'loading') return <p>{ru.app.loading}</p>;
  if (state.kind === 'failed') return <p role="alert">{ru.app.loadError}</p>;
  if (state.kind === 'changePassword')
    return (
      <ChangePasswordForm
        onChanged={(user) => {
          void enter(user).catch(() => {
            setState({ kind: 'failed' });
          });
        }}
      />
    );
  if (state.kind === 'signedOut')
    return (
      <LoginForm
        onSignedIn={(user) => {
          void enter(user).catch(() => {
            setState({ kind: 'failed' });
          });
        }}
      />
    );

  return (
    <>
      <header>
        <span data-testid="current-user">{state.user.name}</span>{' '}
        <button data-testid="logout" onClick={() => void logout()}>
          {ru.login.logout}
        </button>
      </header>
      {state.seasonId ? (
        <SeasonScreen seasonId={state.seasonId} onSignedOut={signedOut} />
      ) : (
        <p data-testid="no-season">{ru.app.noSeason}</p>
      )}
    </>
  );
}
