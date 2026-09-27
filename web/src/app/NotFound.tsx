import type { ReactNode } from 'react';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { EmptyState } from '../ui/States';
import { navigate, paths } from './router';

/** A page that is not there — an address, a profile, a game (D-202): what is missing and the way back to the season */
export function NotFound({ icon, title, text }: { icon: ReactNode; title: string; text: string }) {
  return (
    <EmptyState
      level={1}
      icon={icon}
      title={title}
      text={text}
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
  );
}
