import { KeyRound, LogOut, Map as MapIcon, MessagesSquare, UserRound } from 'lucide-react';
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
import { navigate, paths, routeOf, usePath } from './router';

/** The site's sections: the season (map, turn, leaderboard) and its feed (H5) */
function SiteNav({ className }: { className?: string }) {
  const route = routeOf(usePath());
  const items = [
    {
      to: paths.season(),
      label: ru.feed.nav.season,
      icon: <MapIcon size={18} aria-hidden />,
      current: route.kind === 'season',
    },
    {
      to: paths.feed(),
      label: ru.feed.nav.feed,
      icon: <MessagesSquare size={18} aria-hidden />,
      current: route.kind === 'feed',
    },
  ];
  return (
    <nav aria-label={ru.feed.nav.label} className={className}>
      <ul className="flex gap-1">
        {items.map((item) => (
          <li key={item.to}>
            <Link
              to={item.to}
              aria-current={item.current ? 'page' : undefined}
              className={cx(
                'inline-flex min-h-11 items-center gap-2 rounded-full px-3 whitespace-nowrap is-hover:bg-muted',
                // The current section: filled and bold, not by colour alone
                item.current ? 'bg-muted font-bold text-ink' : 'font-medium text-ink-soft',
              )}
            >
              {item.icon}
              {item.label}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}

/** The frame of every signed-in page: the name of the game, the sections, the connection mark, the bug report and my menu */
export function Shell({
  user,
  onChangePassword,
  onLogout,
  offline,
  children,
}: {
  user: Schemas['CurrentUser'];
  onChangePassword: () => void;
  onLogout: () => void;
  /** The styleguide shows the lost connection without losing it */
  offline?: boolean;
  children: ReactNode;
}) {
  const live = useSyncExternalStore(connectionStatus.subscribe, connectionStatus.get) === 'online';
  const online = offline === undefined ? live : !offline;
  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-10 border-b-2 border-muted bg-page/95 backdrop-blur-sm">
        <div className="mx-auto flex max-w-300 items-center gap-2 px-4 py-2 desk:gap-3 desk:px-8">
          <Link
            to={paths.season()}
            className="mr-auto rounded-md px-1 font-display text-lg font-heavy whitespace-nowrap desk:mr-0"
          >
            {ru.app.title}
          </Link>
          {/* A desktop's sections stand next to the name; a phone's get a row of their own below */}
          <SiteNav className="mr-auto hidden desk:block" />
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
                label: ru.feed.nav.profile,
                icon: <UserRound size={18} aria-hidden />,
                onSelect: () => {
                  navigate(paths.profile(user.id));
                },
                testId: 'my-profile',
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
      {/* Below the sticky header on a phone: the sections scroll away with the page, the header stays small */}
      <SiteNav className="mx-auto max-w-300 border-b-2 border-muted px-4 py-1 desk:hidden" />

      {children}
    </div>
  );
}
