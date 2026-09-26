import { KeyRound, LogOut } from 'lucide-react';
import { useSyncExternalStore, type ReactNode } from 'react';
import { connectionStatus } from '../api/connection';
import type { Schemas } from '../api/client';
import { tokenColors } from '../design/players';
import { ru } from '../i18n/ru';
import { Menu } from '../ui/Menu';
import { ConnectionLost } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { BugReportButton } from './BugReportButton';

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
          <a
            href="/"
            className="mr-auto rounded-md px-1 font-display text-lg font-heavy whitespace-nowrap is-focus:focus-ring"
          >
            {ru.app.title}
          </a>
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
