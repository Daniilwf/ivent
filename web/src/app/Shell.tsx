import {
  BookOpen,
  KeyRound,
  Library,
  LogOut,
  Map as MapIcon,
  MessagesSquare,
  ShieldCheck,
  UserRound,
} from 'lucide-react';
import { useSyncExternalStore, type ReactNode } from 'react';
import { connectionStatus } from '../api/connection';
import type { Schemas } from '../api/client';
import { userToken } from '../design/players';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';
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
];

/**
 * The site's sections (H5–H7): the season (map, turn, leaderboard), its feed, the pool and the rules. One row: under
 * the name on a phone (the icons stay out, the four words fit 390 px), between the name and my menu on a desktop
 */
function SiteNav() {
  const route = routeOf(usePath());
  return (
    <nav
      aria-label={ru.nav.label}
      data-testid="site-nav"
      className="order-last -mx-1 w-full overflow-x-auto p-1 desk:order-none desk:mx-0 desk:mr-auto desk:w-auto"
    >
      <ul className="flex gap-1">
        {sections.map((section) => {
          const current = route.kind === section.kind;
          return (
            <li key={section.kind} className="shrink-0">
              <Link
                to={section.to}
                aria-current={current ? 'page' : undefined}
                data-testid={`nav-${section.kind}`}
                className={cx(
                  'inline-flex min-h-11 items-center gap-2 rounded-full px-4 font-medium whitespace-nowrap transition duration-(--duration-fast) desk:px-3',
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
    </nav>
  );
}

/**
 * The frame of every signed-in page: the name of the game, the sections, the connection mark, the bug report and my
 * menu (my profile, the admin's pages for the admin, my password, sign out)
 */
export function Shell({
  user,
  onChangePassword,
  onLogout,
  offline,
  admin = false,
  children,
}: {
  user: Schemas['CurrentUser'];
  onChangePassword: () => void;
  onLogout: () => void;
  /** The styleguide shows the lost connection without losing it */
  offline?: boolean;
  /** The admin's menu also opens the admin's pages (H8) */
  admin?: boolean;
  children: ReactNode;
}) {
  const live = useSyncExternalStore(connectionStatus.subscribe, connectionStatus.get) === 'online';
  const online = offline === undefined ? live : !offline;
  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-10 border-b-2 border-muted bg-page/95 backdrop-blur-sm">
        <div className="mx-auto flex max-w-300 flex-wrap items-center gap-x-2 gap-y-1 px-4 pt-2 pb-1 desk:flex-nowrap desk:gap-3 desk:px-8 desk:py-2">
          <Link
            to={paths.season()}
            className="mr-auto rounded-md px-1 font-display text-lg font-heavy whitespace-nowrap desk:mr-2"
          >
            {ru.app.title}
          </Link>
          <SiteNav />
          {online ? null : <ConnectionLost compact />}
          <BugReportButton />
          <Menu
            trigger={
              <button
                type="button"
                data-testid="user-menu"
                aria-label={ru.shell.menu(user.name)}
                className="inline-flex min-h-11 shrink-0 cursor-pointer items-center gap-2 rounded-full p-1 is-hover:bg-muted is-focus:focus-ring data-[state=open]:bg-muted desk:pr-3"
              >
                <Sticker
                  player={{
                    name: user.name,
                    token: userToken(user.id),
                    avatar: user.avatar?.thumbnailUrl,
                  }}
                  size={36}
                />
                {/* A phone's header keeps the sticker only; the name is still there for screen readers */}
                <span
                  data-testid="current-user"
                  className="sr-only max-w-40 truncate font-medium desk:not-sr-only"
                >
                  {user.name}
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
                      onSelect: () => {
                        navigate(paths.admin());
                      },
                      testId: 'to-admin',
                    },
                  ]
                : []),
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
