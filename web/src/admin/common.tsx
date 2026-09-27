import type { ReactNode, Ref } from 'react';

/** A section's heading row: the title, a short lead and the count, if any */
export function SectionHead({
  title,
  lead,
  id,
  headingRef,
  action,
}: {
  title: string;
  lead: string;
  id?: string;
  /** The page's heading: it takes the focus when the page is opened (usePageHeading) */
  headingRef?: Ref<HTMLHeadingElement>;
  /** Beside the title: the list of sections on a phone */
  action?: ReactNode;
}) {
  return (
    <div className="grid gap-1">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1
          ref={headingRef}
          id={id}
          tabIndex={-1}
          className="min-w-0 font-display text-xl font-heavy text-balance outline-none desk:text-2xl"
        >
          {title}
        </h1>
        {action}
      </div>
      <p className="max-w-prose text-ink-soft">{lead}</p>
    </div>
  );
}
