import type { ReactNode, Ref } from 'react';
import { Link } from '../app/Link';
import { ru } from '../i18n/ru';
import { standaloneLink } from '../ui/buttonStyles';
import { cx } from '../ui/cx';
import { Chip } from '../ui/Marks';
import { PageHeader } from '../ui/PageHeader';

/** Keeps the section and its navigation in view on a phone. */
export function AdminHeader({
  title,
  lead,
  headingRef,
  action,
  season,
}: {
  title: string;
  lead: string;
  /** The page's heading: it takes the focus when the page is opened (usePageHeading) */
  headingRef?: Ref<HTMLHeadingElement>;
  /** Beside the title: the list of sections on a phone */
  action?: ReactNode;
  season: { label: string; href: string } | null;
}) {
  return (
    <>
      <div className="grid min-w-0 grid-cols-[minmax(0,1fr)_auto] items-start gap-2 max-desk:sticky max-desk:top-(--shell-header-height) max-desk:z-5 max-desk:-mx-4 max-desk:bg-page max-desk:px-4 max-desk:py-2 desk:gap-1">
        <div className="grid min-w-0 gap-1 desk:flex desk:flex-wrap desk:items-center desk:gap-x-3">
          <PageHeader
            compact
            ref={headingRef}
            id="admin-heading"
            tabIndex={-1}
            className="min-w-0 text-balance wrap-break-word outline-none"
          >
            {title}
          </PageHeader>
          {season ? (
            <span className="min-w-0" data-testid="admin-season-name">
              <Chip>
                <span className="min-w-0 truncate" title={season.label}>
                  {season.label}
                </span>
              </Chip>
            </span>
          ) : null}
        </div>
        {action}
      </div>
      <div className="flex min-w-0 items-baseline justify-between gap-3 desk:-mt-3">
        <p
          className="min-w-0 max-w-prose text-ink-soft max-desk:line-clamp-1 max-desk:text-sm"
          title={lead}
        >
          {lead}
        </p>
        {season ? (
          <Link to={season.href} className={cx(standaloneLink, 'shrink-0')}>
            {ru.admin.changeSeason}
          </Link>
        ) : null}
      </div>
    </>
  );
}
