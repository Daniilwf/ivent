import {
  BookOpen,
  ChevronDown,
  Images,
  KeyRound,
  Library,
  LogOut,
  Map as MapIcon,
  MessagesSquare,
  Newspaper,
  ShieldCheck,
  Trophy,
  UserRound,
  UsersRound,
  Vote,
} from 'lucide-react';
import { useLayoutEffect, useRef, useSyncExternalStore, type ReactNode } from 'react';
import { connectionStatus } from '../api/connection';
import { useWaitingProofs } from '../admin/waitingProofs';
import type { Schemas } from '../api/client';
import { userToken } from '../design/players';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';
import { CountBadge } from '../ui/Marks';
import { Menu } from '../ui/Menu';
import { ConnectionLost } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { BugReportButton } from './BugReportButton';
import { Link } from './Link';
import { navigate, paths, routeOf, usePath, type Route } from './router';

const sections: { kind: Route['kind'] & keyof typeof ru.nav; to: string; icon: ReactNode }[] = [
  { kind: 'season', to: paths.season(), icon: <MapIcon size={18} aria-hidden /> },
  { kind: 'feed', to: paths.feed(), icon: <MessagesSquare size={18} aria-hidden /> },
  { kind: 'pool', to: paths.pool(), icon: <Library size={18} aria-hidden /> },
  { kind: 'rules', to: paths.rules(), icon: <BookOpen size={18} aria-hidden /> },
  { kind: 'hallOfFame', to: paths.hallOfFame(), icon: <Trophy size={18} aria-hidden /> },
];

const navControl =
  'inline-flex min-h-11 min-w-11 items-center justify-center gap-2 rounded-full px-2 text-sm font-medium whitespace-nowrap transition duration-(--duration-fast) desk:px-3 desk:text-base';

/** Primary links share a row with the seasonal menu. On a phone the primary links can scroll. */
function SiteNav({
  admin,
  pollsSeasonId,
  gallerySeasonId,
  interactionsSeasonId,
}: {
  admin: boolean;
  pollsSeasonId: string | null;
  gallerySeasonId: string | null;
  interactionsSeasonId: string | null;
}) {
  const found = routeOf(usePath());
  // Anyone but the admin at the admin's address sees the game: its section is the current one
  const route: Route = found.kind === 'admin' && !admin ? { kind: 'season' } : found;
  const seasonal: { kind: Route['kind']; label: string; to: string; icon: ReactNode }[] = [];
  if (gallerySeasonId) {
    seasonal.push({
      kind: 'gallery',
      label: ru.nav.gallery,
      to: paths.gallery(gallerySeasonId),
      icon: <Images size={18} aria-hidden />,
    });
  }
  if (pollsSeasonId) {
    seasonal.push({
      kind: 'polls',
      label: ru.nav.polls,
      to: paths.polls(pollsSeasonId),
      icon: <Vote size={18} aria-hidden />,
    });
  }
  if (interactionsSeasonId) {
    seasonal.push({
      kind: 'interactions',
      label: ru.turn.interactions,
      to: paths.interactions(interactionsSeasonId),
      icon: <UsersRound size={18} aria-hidden />,
    });
  }

  return (
    <nav
      aria-label={ru.nav.label}
      data-testid="site-nav"
      className="order-last -mx-1 flex w-full min-w-0 items-center gap-1 desk:order-none desk:mx-0 desk:w-auto desk:flex-1"
    >
      <ul className="scrollbar-none -m-1 flex min-w-0 gap-1 overflow-x-auto p-2">
        {sections.map((section) => {
          const current = route.kind === section.kind;
          return (
            <li key={section.kind} className="shrink-0">
              <Link
                to={section.to}
                aria-current={current ? 'page' : undefined}
                data-testid={`nav-${section.kind}`}
                onFocus={({ currentTarget }) => {
                  currentTarget.scrollIntoView({ block: 'nearest', inline: 'nearest' });
                }}
                className={cx(
                  navControl,
                  'scroll-mx-2',
                  // The current section: filled, not by colour alone
                  current ? 'bg-ink text-on-color' : 'text-ink is-hover:bg-muted',
                )}
              >
                <span className="hidden desk:contents">{section.icon}</span>
                {ru.nav[section.kind]}
              </Link>
            </li>
          );
        })}
      </ul>
      {seasonal.length ? (
        <Menu
          trigger={
            <button
              type="button"
              data-testid="nav-more"
              className={cx(
                navControl,
                'shrink-0 cursor-pointer is-focus:focus-ring',
                seasonal.some((section) => section.kind === route.kind)
                  ? 'bg-ink text-on-color'
                  : 'text-ink is-hover:bg-muted data-[state=open]:bg-muted',
              )}
            >
              {ru.nav.more}
              <ChevronDown size={16} aria-hidden />
            </button>
          }
          items={seasonal.map((section) => ({
            label: section.label,
            icon: section.icon,
            testId: `nav-${section.kind}`,
            link: (
              <Link
                to={section.to}
                aria-current={route.kind === section.kind ? 'page' : undefined}
              />
            ),
          }))}
        />
      ) : null}
    </nav>
  );
}

/**
 * The frame of every signed-in page: the name of the game, the sections, the connection mark, the bug report and my
 * menu (my profile, the admin's pages for the admin, the release journal, my password, sign out)
 */
export function Shell({
  user,
  seasonId = null,
  pollsSeasonId = null,
  gallerySeasonId = null,
  interactionsSeasonId = null,
  onChangePassword,
  onLogout,
  offline,
  waiting: shownInStyleguide,
  children,
}: {
  user: Schemas['CurrentUser'];
  /** The current season: the admin's count of proofs to check is its (D-263) */
  seasonId?: string | null;
  pollsSeasonId?: string | null;
  gallerySeasonId?: string | null;
  interactionsSeasonId?: string | null;
  onChangePassword: () => void;
  onLogout: () => void;
  /** The styleguide shows the lost connection without losing it */
  offline?: boolean;
  /** The styleguide shows the admin's count of proofs to check without a season */
  waiting?: number;
  children: ReactNode;
}) {
  // The admin's menu also opens the admin's pages (H8)
  const admin = user.role === 'admin';
  // On the admin page the page's own queue reads give the count; elsewhere the header reads it
  const onAdminPage = routeOf(usePath()).kind === 'admin';
  const read = useWaitingProofs(admin ? seasonId : null, admin && !onAdminPage) ?? 0;
  const waiting = shownInStyleguide ?? read;
  const shownWaiting = admin && waiting > 0 ? waiting : 0;
  const live = useSyncExternalStore(connectionStatus.subscribe, connectionStatus.get) === 'online';
  const online = offline === undefined ? live : !offline;
  const frame = useRef<HTMLDivElement>(null);
  const header = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    const layout = frame.current;
    const bar = header.current;
    if (!layout || !bar) return;

    // The season and admin use the actual header height to fit their sticky layout to the viewport.
    const measure = () => {
      const height = bar.getBoundingClientRect().height;
      if (height > 0) layout.style.setProperty('--shell-header-height', `${height}px`);
    };
    measure();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(bar, { box: 'border-box' });
    return () => {
      observer?.disconnect();
      layout.style.removeProperty('--shell-header-height');
    };
  }, []);
  return (
    <div ref={frame} className="shell-min-height">
      <header
        ref={header}
        className="sticky top-0 z-10 border-b-2 border-muted bg-page/95 backdrop-blur-sm"
      >
        <div className="mx-auto flex max-w-300 flex-wrap items-center gap-x-2 gap-y-1 px-4 pt-2 pb-1 desk:flex-nowrap desk:gap-3 desk:px-8 desk:py-2">
          <Link
            to={paths.season()}
            className="mr-auto inline-flex min-h-11 items-center rounded-md px-1 font-display text-lg font-heavy whitespace-nowrap desk:mr-2"
          >
            {ru.app.title}
          </Link>
          <SiteNav
            admin={admin}
            pollsSeasonId={pollsSeasonId}
            gallerySeasonId={gallerySeasonId}
            interactionsSeasonId={interactionsSeasonId}
          />
          {online ? null : <ConnectionLost compact />}
          <BugReportButton />
          <Menu
            trigger={
              <button
                type="button"
                data-testid="user-menu"
                aria-label={
                  shownWaiting > 0
                    ? ru.shell.menuWaiting(user.name, shownWaiting)
                    : ru.shell.menu(user.name)
                }
                className="inline-flex min-h-11 shrink-0 cursor-pointer items-center gap-2 rounded-full p-1 is-hover:bg-muted is-focus:focus-ring data-[state=open]:bg-muted desk:pr-3"
              >
                <span className="relative">
                  <Sticker
                    player={{
                      name: user.name,
                      token: userToken(user.id, user.token),
                      avatar: user.avatar?.thumbnailUrl,
                    }}
                    size={36}
                  />
                  {/* D-263: the admin's runs waiting for a proof check, on the way to «Админка» */}
                  {shownWaiting > 0 ? (
                    <CountBadge
                      count={shownWaiting}
                      data-testid="admin-waiting"
                      className="absolute -top-1 -right-2 border-2 border-page"
                    />
                  ) : null}
                </span>
                {/* A phone's header keeps the sticker only; the name is still there for screen readers */}
                <span data-testid="current-user" className="sr-only font-medium desk:not-sr-only">
                  {/* A long name is cut, not pushed past the header (K1A): not-sr-only shows overflow and wraps */}
                  <span className="inline-block max-w-40 truncate align-middle">{user.name}</span>
                </span>
              </button>
            }
            items={[
              {
                label: ru.nav.profile,
                icon: <UserRound size={18} aria-hidden />,
                onSelect: () => {
                  navigate(paths.profile(user.id));
                },
                testId: 'my-profile',
              },
              ...(admin
                ? [
                    {
                      label: ru.nav.admin,
                      icon: <ShieldCheck size={18} aria-hidden />,
                      ...(shownWaiting > 0
                        ? {
                            badge: <CountBadge count={shownWaiting} data-testid="admin-waiting" />,
                            ariaLabel: ru.nav.adminWaiting(shownWaiting),
                          }
                        : {}),
                      onSelect: () => {
                        navigate(paths.admin());
                      },
                      testId: 'to-admin',
                    },
                  ]
                : []),
              {
                label: ru.whatsNew.title,
                icon: <Newspaper size={18} aria-hidden />,
                onSelect: () => {
                  navigate(paths.whatsNew());
                },
              },
              {
                label: ru.shell.changePassword,
                icon: <KeyRound size={18} aria-hidden />,
                onSelect: onChangePassword,
                testId: 'change-password',
              },
              {
                label: ru.login.logout,
                icon: <LogOut size={18} aria-hidden />,
                onSelect: onLogout,
                testId: 'logout',
              },
            ]}
          />
        </div>
      </header>
      {children}
    </div>
  );
}
