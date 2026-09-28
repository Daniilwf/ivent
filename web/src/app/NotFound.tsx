import { SearchX } from 'lucide-react';
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

/** An address the site does not have (D-202, H9): the whole page says so */
export function NotFoundPage() {
  return (
    <main className="mx-auto grid max-w-110 px-4 py-10" data-testid="page-not-found">
      <NotFound
        icon={<SearchX size={28} aria-hidden />}
        title={ru.feed.pageNotFoundTitle}
        text={ru.feed.pageNotFoundText}
      />
    </main>
  );
}
