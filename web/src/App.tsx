import { useCallback, useEffect, useState } from 'react';
import { api, refreshCsrf, type Schemas } from './api/client';
import { CalendarClock, LoaderCircle } from 'lucide-react';
import { ChangePasswordForm } from './app/ChangePasswordForm';
import { LoginForm, TablePage } from './app/LoginForm';
import { MaintenanceBanner } from './app/MaintenanceBanner';
import { Shell } from './app/Shell';
import { ru } from './i18n/ru';
import { Button } from './ui/Button';
import { EmptyState, ErrorState } from './ui/States';
import { SeasonScreen } from './season/SeasonScreen';

type State =
  | { kind: 'loading' }
  | { kind: 'failed' }
  | { kind: 'signedOut' }
  | { kind: 'changePassword' }
  | {
      kind: 'signedIn';
      user: Schemas['CurrentUser'];
      seasonId: string | null;
      ownPassword?: boolean;
    };

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

  // Every screen: the maintenance banner on top; a signed-in page stands in the shell (the bug report is in its header)
  return (
    <>
      <MaintenanceBanner />
      {screen()}
    </>
  );

  function screen() {
    if (state.kind === 'loading')
      return (
        <main className="grid min-h-dvh place-items-center" aria-busy="true">
          <p className="flex items-center gap-2 text-ink-soft">
            <LoaderCircle
              size={20}
              className="animate-spin motion-reduce:animate-none"
              aria-hidden
            />
            {ru.app.loading}
          </p>
        </main>
      );
    if (state.kind === 'failed')
      return (
        <main className="mx-auto grid min-h-dvh max-w-110 place-items-center px-4">
          <ErrorState
            title={ru.shell.loadErrorTitle}
            text={ru.shell.loadErrorText}
            onRetry={() => {
              globalThis.location.reload();
            }}
          />
        </main>
      );
    if (state.kind === 'changePassword')
      return (
        <TablePage>
          <ChangePasswordForm
            onChanged={(user) => {
              void enter(user).catch(() => {
                setState({ kind: 'failed' });
              });
            }}
          >
            <Button variant="link" data-testid="logout" onClick={() => void logout()}>
              {ru.login.logout}
            </Button>
          </ChangePasswordForm>
        </TablePage>
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

    const signedIn = state;
    return (
      <Shell
        user={signedIn.user}
        onChangePassword={() => {
          setState({ ...signedIn, ownPassword: true });
        }}
        onLogout={() => void logout()}
      >
        {signedIn.ownPassword ? (
          <main className="mx-auto grid max-w-110 gap-4 px-4 py-6">
            <ChangePasswordForm
              temporary={false}
              onChanged={(user) => {
                setState({ ...signedIn, user, ownPassword: false });
              }}
            >
              <Button
                variant="link"
                onClick={() => {
                  setState({ ...signedIn, ownPassword: false });
                }}
              >
                {ru.password.back}
              </Button>
            </ChangePasswordForm>
          </main>
        ) : signedIn.seasonId ? (
          // Until stage H2 rebuilds it from the design system, the season screen keeps plain form styles
          <div className="legacy-screens">
            <SeasonScreen seasonId={signedIn.seasonId} onSignedOut={signedOut} />
          </div>
        ) : (
          <main className="mx-auto grid max-w-110 px-4 py-10" data-testid="no-season">
            <EmptyState
              icon={<CalendarClock size={28} aria-hidden />}
              title={ru.shell.noSeasonTitle}
              text={ru.shell.noSeasonText}
            />
          </main>
        )}
      </Shell>
    );
  }
}
