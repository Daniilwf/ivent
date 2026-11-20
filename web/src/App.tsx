import { lazy, Suspense, useCallback, useEffect, useState } from 'react';
import { api, refreshCsrf, SIGNED_OUT_EVENT, type Schemas } from './api/client';
import { CalendarClock, LoaderCircle } from 'lucide-react';
import { ChangePasswordForm } from './app/ChangePasswordForm';
import { LoginForm, TablePage } from './app/LoginForm';
import { EnvironmentBanner } from './app/EnvironmentBanner';
import { MaintenanceBanner } from './app/MaintenanceBanner';
import { onReopen, routeOf, usePath } from './app/router';
import { UpdateBanner } from './app/WhatsNew';
import { NotFoundPage } from './app/NotFound';
import { Shell } from './app/Shell';
import { answerOf, useLoaded } from './app/useLoaded';
import { useSeasonVersion } from './app/useSeasonVersion';
import { ru } from './i18n/ru';
import { Button } from './ui/Button';
import { EmptyState, ErrorState } from './ui/States';
import { SeasonScreen } from './season/SeasonScreen';

// The main page (the season's map and turn) is what a player opens first, over a mobile network within 2.5 s (DESIGN.md
// «Производительность», FL-3): every other screen loads apart, when it is opened, and stays out of the first download
const AdminScreen = lazy(async () => ({
  default: (await import('./admin/AdminScreen')).AdminScreen,
}));
const FeedScreen = lazy(async () => ({ default: (await import('./feed/FeedScreen')).FeedScreen }));
const GalleryScreen = lazy(async () => ({
  default: (await import('./gallery/GalleryScreen')).GalleryScreen,
}));
const GameScreen = lazy(async () => ({ default: (await import('./feed/GameScreen')).GameScreen }));
const ProfileScreen = lazy(async () => ({
  default: (await import('./feed/ProfileScreen')).ProfileScreen,
}));
const SeasonRecap = lazy(async () => ({
  default: (await import('./seasonRecap/SeasonRecap')).SeasonRecap,
}));
const PoolScreen = lazy(async () => ({ default: (await import('./pool/PoolScreen')).PoolScreen }));
const RulesScreen = lazy(async () => ({
  default: (await import('./rules/RulesScreen')).RulesScreen,
}));
const PollsScreen = lazy(async () => ({
  default: (await import('./polls/PollsScreen')).PollsScreen,
}));
const InteractionsScreen = lazy(async () => ({
  default: (await import('./interactions/InteractionsScreen')).InteractionsScreen,
}));

/** The admin's section of an address: none is the proof queue, an unknown one is «Такой страницы нет» */
const knownAdminSection = (section: string | null) =>
  section === null || Object.hasOwn(ru.admin.sections, section);

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

type CurrentSeason = ReturnType<typeof askCurrentSeason>;

/** The season the signed-in user sees by default; a lost answer reads as a failed one */
function askCurrentSeason() {
  return api
    .GET('/api/seasons/current')
    .catch(() => ({ data: undefined, response: new Response(null, { status: 503 }) }));
}

export function App() {
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [homeNavigation, setHomeNavigation] = useState<Schemas['SeasonNavigationView'] | null>(
    null,
  );
  const path = usePath();
  const route = routeOf(path);

  const signedOut = useCallback(() => {
    setState({ kind: 'signedOut' });
  }, []);

  const navigationSeason =
    state.kind === 'signedIn'
      ? ((
          (route.kind === 'feed' ||
          route.kind === 'gallery' ||
          route.kind === 'polls' ||
          route.kind === 'interactions'
            ? route.seasonId
            : null) ?? state.seasonId
        )?.toLowerCase() ?? null)
      : null;
  const showingHome =
    state.kind === 'signedIn' &&
    state.ownPassword !== path &&
    (route.kind === 'season' || (route.kind === 'admin' && state.user.role !== 'admin'));
  // The home view already refreshes on its own subscription and includes the flag.
  if (!showingHome && homeNavigation !== null) setHomeNavigation(null);
  const remoteNavigationSeason = showingHome ? null : navigationSeason;
  const navigationVersion = useSeasonVersion(remoteNavigationSeason, {
    refreshOnJoin: true,
    relevant: (update) =>
      update.seasonId.toLowerCase() === remoteNavigationSeason &&
      update.types.some((type) => type === 'ruleset-changed' || type === 'command-undone'),
  });
  const loadNavigation = useCallback(async () => {
    if (!remoteNavigationSeason) return { kind: 'loading' } as const;
    return answerOf(
      await api.GET('/api/seasons/{seasonId}/navigation', {
        params: { path: { seasonId: remoteNavigationSeason } },
      }),
    );
  }, [remoteNavigationSeason]);
  const remoteNavigation = useLoaded(loadNavigation, {
    version: navigationVersion,
    onSignedOut: signedOut,
  });
  const navigation = showingHome
    ? homeNavigation
    : remoteNavigation.kind === 'ready'
      ? remoteNavigation.value
      : null;
  const gallerySeasonId =
    navigation?.id.toLowerCase() === navigationSeason && navigation.galleryEnabled
      ? navigationSeason
      : null;
  const pollsSeasonId =
    navigation?.id.toLowerCase() === navigationSeason && navigation.pollsEnabled
      ? navigationSeason
      : null;

  // A session ended elsewhere (another device, the admin, D-466): any 401 brings the sign-in form at once
  useEffect(() => {
    const onSignedOut = () => {
      setState((now) =>
        now.kind === 'signedIn' || now.kind === 'changePassword' ? { kind: 'signedOut' } : now,
      );
    };
    globalThis.addEventListener(SIGNED_OUT_EVENT, onSignedOut);
    return () => {
      globalThis.removeEventListener(SIGNED_OUT_EVENT, onSignedOut);
    };
  }, []);

  const enter = useCallback(async (user: Schemas['CurrentUser'], asked?: CurrentSeason) => {
    // A temporary password opens nothing but its change (D-106)
    if (user.mustChangePassword) {
      setState({ kind: 'changePassword' });
      return;
    }

    const { data, response } = await (asked ?? askCurrentSeason());
    if (data) {
      setHomeNavigation(null);
      setState({ kind: 'signedIn', user, seasonId: data.id });
    } else if (response.status === 404) setState({ kind: 'signedIn', user, seasonId: null });
    else if (response.status === 401) setState({ kind: 'signedOut' });
    else setState({ kind: 'failed' });
  }, []);

  useEffect(() => {
    void (async () => {
      try {
        // At once, not one after another (I1-1): on a slow mobile network each is a round trip before the season loads.
        // The season is asked on the chance of a session; without one its 401 is never read.
        const season = askCurrentSeason();
        const [, { data, response }] = await Promise.all([refreshCsrf(), api.GET('/api/auth/me')]);
        if (data) await enter(data, season);
        else if (response.status === 401) setState({ kind: 'signedOut' });
        else setState({ kind: 'failed' });
      } catch {
        setState({ kind: 'failed' });
      }
    })();
  }, [enter]);

  // A link to the page under the password form opens that page: the form closes (D-150)
  useEffect(
    () =>
      onReopen(() => {
        setState((s) => (s.kind === 'signedIn' && s.ownPassword ? { ...s, ownPassword: null } : s));
      }),
    [],
  );

  async function logout() {
    try {
      await api.POST('/api/auth/logout');
      await refreshCsrf();
    } finally {
      setState({ kind: 'signedOut' });
    }
  }

  // Every screen: which copy of the site this is (none on the live site) and the maintenance banner on top; a
  // signed-in page stands in the shell (the bug report is in its header)
  return (
    <>
      <EnvironmentBanner />
      <MaintenanceBanner />
      <UpdateBanner />
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
          <Loading />
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
    // The admin's pages (H8) open for the admin only; anyone else at their address sees the game
    const admin = signedIn.user.role === 'admin';
    return (
      <Shell
        user={signedIn.user}
        seasonId={signedIn.seasonId}
        pollsSeasonId={pollsSeasonId}
        gallerySeasonId={gallerySeasonId}
        onChangePassword={() => {
          setState({ ...signedIn, ownPassword: path });
        }}
        onLogout={() => void logout()}
      >
        <Suspense
          fallback={
            <main className="grid place-items-center px-4 py-10" aria-busy="true">
              <Loading />
            </main>
          }
        >
          {page()}
        </Suspense>
      </Shell>
    );

    function page() {
      return signedIn.ownPassword === path ? (
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
      ) : route.kind === 'admin' && admin && knownAdminSection(route.section) ? (
        <AdminScreen
          section={route.section}
          currentSeasonId={signedIn.seasonId}
          user={signedIn.user}
        />
      ) : route.kind === 'profile' ? (
        <ProfileScreen
          key={route.userId}
          userId={route.userId}
          meId={signedIn.user.id}
          onSignedOut={signedOut}
        />
      ) : route.kind === 'seasonRecap' ? (
        <SeasonRecap
          key={route.seasonId}
          seasonId={route.seasonId}
          user={signedIn.user}
          onSignedOut={signedOut}
        />
      ) : route.kind === 'game' ? (
        <GameScreen key={route.gameId} gameId={route.gameId} onSignedOut={signedOut} />
      ) : route.kind === 'notFound' || (route.kind === 'admin' && admin) ? (
        // An address the site does not have, the admin's unknown section included
        <NotFoundPage />
      ) : route.kind === 'feed' && (route.seasonId ?? signedIn.seasonId) ? (
        <FeedScreen
          key={route.seasonId ?? signedIn.seasonId}
          seasonId={(route.seasonId ?? signedIn.seasonId) as string}
          onSignedOut={signedOut}
        />
      ) : route.kind === 'gallery' ? (
        <GalleryScreen
          key={route.seasonId?.toLowerCase() ?? ''}
          requestedSeasonId={route.seasonId?.toLowerCase() ?? null}
          currentSeasonId={signedIn.seasonId?.toLowerCase() ?? null}
          admin={admin}
          onSignedOut={signedOut}
        />
      ) : route.kind === 'polls' && navigationSeason ? (
        <PollsScreen key={navigationSeason} seasonId={navigationSeason} onSignedOut={signedOut} />
      ) : route.kind === 'interactions' ? (
        <InteractionsScreen
          key={navigationSeason}
          seasonId={navigationSeason}
          role={signedIn.user.role}
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
        <SeasonScreen
          seasonId={signedIn.seasonId}
          role={signedIn.user.role}
          onSignedOut={signedOut}
          onNavigationLoaded={setHomeNavigation}
        />
      ) : (
        <main className="mx-auto grid max-w-110 px-4 py-10" data-testid="no-season">
          <EmptyState
            level={1}
            icon={<CalendarClock size={28} aria-hidden />}
            title={ru.shell.noSeasonTitle}
            text={ru.shell.noSeasonText}
          />
        </main>
      );
    }
  }
}

function Loading() {
  return (
    <p className="flex items-center gap-2 rounded-full bg-card px-4 py-2 font-medium">
      <LoaderCircle size={20} className="animate-spin motion-reduce:animate-none" aria-hidden />
      {ru.ui.loading}
    </p>
  );
}
