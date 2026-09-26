import { KeyRound, LogOut } from 'lucide-react';
import { useSyncExternalStore, type ReactNode } from 'react';
import { connectionStatus } from '../api/connection';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Menu } from '../ui/Menu';
import { ConnectionLost } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { BugReportButton } from './BugReportButton';

/** A stable token colour for a user outside a season: by their id */
function userToken(id: string) {
  let hash = 0;
  for (const ch of id) hash = (hash * 31 + ch.charCodeAt(0)) | 0;
  return Math.abs(hash) % 16;
}

/** The frame of every signed-in page: the name of the game, the connection mark, the bug report and my menu */
export function Shell({
  user,
  onChangePassword,
  onLogout,
  children,
}: {
  user: Schemas['CurrentUser'];
  onChangePassword: () => void;
  onLogout: () => void;
  children: ReactNode;
}) {
  const online =
    useSyncExternalStore(connectionStatus.subscribe, connectionStatus.get) === 'online';
  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-10 border-b-2 border-muted bg-page/95 backdrop-blur-sm">
        <div className="mx-auto flex max-w-300 items-center gap-3 px-4 py-2 desk:px-8">
          <a
            href="/"
            className="mr-auto rounded-md font-display text-lg font-heavy"
            aria-label={ru.shell.home}
          >
            {ru.app.title}
          </a>
          {online ? null : <ConnectionLost />}
          <BugReportButton />
          <Menu
            trigger={
              <button
                type="button"
                data-testid="user-menu"
                aria-label={ru.shell.menu(user.name)}
                className="inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-full py-1 pr-3 pl-1 is-hover:bg-muted is-focus:focus-ring"
              >
                <Sticker
                  player={{
                    name: user.name,
                    token: userToken(user.id),
                    avatar: user.avatar?.thumbnailUrl,
                  }}
                  size={36}
                />
                <span
                  data-testid="current-user"
                  className="max-w-28 truncate font-medium desk:max-w-40"
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
                label: ru.shell.logout,
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
