import { Newspaper } from 'lucide-react';
import { ru } from '../i18n/ru';
import { PageHeader } from '../ui/PageHeader';
import { EmptyState } from '../ui/States';
import changelog from '../whatsNew/changelog.json';
import { usePageHeading, useRestoredScroll } from './router';
import { ReleaseList, type Release } from './WhatsNew';

export function WhatsNewScreen({ releases = changelog }: { releases?: Release[] }) {
  useRestoredScroll(true);
  const heading = usePageHeading();
  return (
    <main className="mx-auto grid max-w-180 gap-5 px-4 py-6 desk:px-8 desk:py-8">
      <PageHeader ref={heading} tabIndex={-1} className="outline-none">
        {ru.whatsNew.title}
      </PageHeader>
      {releases.length === 0 ? (
        <EmptyState
          icon={<Newspaper size={28} aria-hidden />}
          title={ru.whatsNew.empty}
          level={2}
        />
      ) : (
        <div className="min-w-0 rounded-lg bg-card p-5 desk:p-6">
          <ReleaseList
            releases={releases}
            headingLevel={2}
            className="divide-y-2 divide-muted *:not-last:pb-5"
          />
        </div>
      )}
    </main>
  );
}
