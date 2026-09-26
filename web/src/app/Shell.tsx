import { KeyRound, LogOut } from 'lucide-react';
import { useSyncExternalStore, type MouseEvent, type ReactNode } from 'react';
import { connectionStatus } from '../api/connection';
import type { Schemas } from '../api/client';
import { tokenColors } from '../design/players';
import { ru } from '../i18n/ru';
import { Menu } from '../ui/Menu';
import { ConnectionLost } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { cx } from '../ui/cx';
import { BugReportButton } from './BugReportButton';
import { navigate, pagePaths, type Page } from './nav';

const sections: Page[] = ['home', 'pool', 'rules'];

/** A plain click opens the section in place; a click with a modifier opens a tab as a link does */
function follow(event: MouseEvent<HTMLAnchorElement>, page: Page) {
  if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)
    return;
  event.preventDefault();
  navigate(page);
}

/** A stable token colour for a user outside a season: by their id */
function userToken(id: string) {
  let hash = 0;
  for (const ch of id) hash = (hash * 31 + ch.charCodeAt(0)) | 0;
  return Math.abs(hash) % tokenColors.length;
}

/** The frame of every signed-in page: the name of the game, the connection mark, the bug report and my menu */
export function Shell({
  user,
  onChangePassword,
  onLogout,
  offline,
  page = 'home',
  children,
}: {
  user: Schemas['CurrentUser'];
  onChangePassword: () => void;
  onLogout: () => void;
  /** The styleguide shows the lost connection without losing it */
  offline?: boolean;
  /** The section open now: its link is marked */
  page?: Page;
  children: ReactNode;
}) {
  const live = useSyncExternalStore(connectionStatus.subscribe, connectionStatus.get) === 'online';
  const online = offline === undefined ? live : !offline;
  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-10 border-b-2 border-muted bg-page/95 backdrop-blur-sm">
        <div className="mx-auto flex max-w-300 flex-wrap items-center gap-x-2 gap-y-1 px-4 pt-2 pb-1 desk:gap-3 desk:px-8 desk:py-2">
          <a
            href="/"
            className="mr-auto rounded-md px-1 font-display text-lg font-heavy whitespace-nowrap is-focus:focus-ring desk:mr-2"
            onClick={(event) => {
              follow(event, 'home');
            }}
          >
            {ru.app.title}
          </a>
          {/* A phone keeps the sections in a row of their own under the name; a desktop between the name and my menu */}
          <nav
            aria-label={ru.nav.label}
            data-testid="site-nav"
            className="order-last -mx-1 flex w-full gap-1 overflow-x-auto p-1 desk:order-none desk:mx-0 desk:mr-auto desk:w-auto"
          >
            {sections.map((section) => (
              <a
                key={section}
                href={pagePaths[section]}
                aria-current={section === page ? 'page' : undefined}
                data-testid={`nav-${section}`}
                onClick={(event) => {
                  follow(event, section);
                }}
                className={cx(
                  'inline-flex min-h-11 shrink-0 items-center rounded-full px-4 font-medium whitespace-nowrap transition duration-(--duration-fast) is-hover:bg-muted is-focus:focus-ring',
                  section === page ? 'bg-ink text-on-color is-hover:bg-ink' : 'text-ink',
                )}
              >
                {ru.nav[section]}
              </a>
            ))}
          </nav>
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
