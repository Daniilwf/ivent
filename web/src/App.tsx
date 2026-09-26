import { useCallback, useEffect, useState } from 'react';
import { api, refreshCsrf, type Schemas } from './api/client';
import { CalendarClock, LoaderCircle, SearchX } from 'lucide-react';
import { ChangePasswordForm } from './app/ChangePasswordForm';
import { LoginForm, TablePage } from './app/LoginForm';
import { MaintenanceBanner } from './app/MaintenanceBanner';
import { navigate, paths, routeOf, usePath } from './app/router';
import { Shell } from './app/Shell';
import { FeedScreen } from './feed/FeedScreen';
import { GameScreen } from './feed/GameScreen';
import { ProfileScreen } from './feed/ProfileScreen';
import { ru } from './i18n/ru';
import { Button } from './ui/Button';
import { EmptyState, ErrorState } from './ui/States';
import { SeasonScreen } from './season/SeasonScreen';
import { PoolScreen } from './pool/PoolScreen';
import { RulesScreen } from './rules/RulesScreen';

type State =
  | { kind: 'loading' }
  | { kind: 'failed' }
  | { kind: 'signedOut' }
  | { kind: 'changePassword' }
  | {
      kind: 'signedIn';
      user: Schemas['CurrentUser'];
      seasonId: string | null;
      /** The password form opened over this page; another page closes it */
      ownPassword?: string | null;
    };

export function App() {
  const [state, setState] = useState<State>({ kind: 'loading' });
  const path = usePath();
  const route = routeOf(path);

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

  // Back from the password form, the focus returns to the menu that opened it
  function focusMenu() {
    requestAnimationFrame(() => {
      document.querySelector<HTMLElement>('[data-testid="user-menu"]')?.focus();
    });
  }

  function screen() {
    if (state.kind === 'loading')
      return (
        // On the table, like the sign-in that most likely comes next: no grey flash before it
        <main className="table-surface grid min-h-dvh place-items-center" aria-busy="true">
          <p className="flex items-center gap-2 rounded-full bg-card px-4 py-2 font-medium">
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
            level={1}
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
          setState({ ...signedIn, ownPassword: path });
        }}
        onLogout={() => void logout()}
      >
        {signedIn.ownPassword === path ? (
          <main className="mx-auto grid max-w-110 gap-4 px-4 py-6">
            <ChangePasswordForm
              temporary={false}
              onChanged={(user) => {
                setState({ ...signedIn, user, ownPassword: null });
                focusMenu();
              }}
            >
              <Button
                variant="link"
                onClick={() => {
                  setState({ ...signedIn, ownPassword: null });
                  focusMenu();
                }}
              >
                {ru.password.back}
              </Button>
            </ChangePasswordForm>
          </main>
        ) : route.kind === 'profile' ? (
          <ProfileScreen
            key={route.userId}
            userId={route.userId}
            meId={signedIn.user.id}
            onSignedOut={signedOut}
          />
        ) : route.kind === 'game' ? (
          <GameScreen key={route.gameId} gameId={route.gameId} onSignedOut={signedOut} />
        ) : route.kind === 'notFound' ? (
          <main className="mx-auto grid max-w-110 px-4 py-10" data-testid="page-not-found">
            <EmptyState
              level={1}
              icon={<SearchX size={28} aria-hidden />}
              title={ru.feed.pageNotFoundTitle}
              text={ru.feed.pageNotFoundText}
              action={
                <Button
                  variant="main"
                  onClick={() => {
                    navigate(paths.season());
                  }}
                >
                  {ru.profile.toSeason}
                </Button>
              }
            />
          </main>
        ) : route.kind === 'feed' && (route.seasonId ?? signedIn.seasonId) ? (
          <FeedScreen
            key={route.seasonId ?? signedIn.seasonId}
            seasonId={(route.seasonId ?? signedIn.seasonId) as string}
            onSignedOut={signedOut}
          />
        ) : route.kind === 'pool' ? (
          <PoolScreen
            seasonId={signedIn.seasonId}
            canAdd={signedIn.user.role !== 'spectator'}
            onSignedOut={signedOut}
          />
        ) : route.kind === 'rules' ? (
          <RulesScreen seasonId={signedIn.seasonId} onSignedOut={signedOut} />
        ) : signedIn.seasonId ? (
          <SeasonScreen seasonId={signedIn.seasonId} onSignedOut={signedOut} />
        ) : (
          <main className="mx-auto grid max-w-110 px-4 py-10" data-testid="no-season">
            <EmptyState
              level={1}
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
