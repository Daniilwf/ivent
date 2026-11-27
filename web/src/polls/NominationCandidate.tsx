import type { Ref } from 'react';
import type { Schemas } from '../api/client';
import { Link } from '../app/Link';
import { ru } from '../i18n/ru';
import { standaloneLink } from '../ui/buttonStyles';
import { cx } from '../ui/cx';

type Candidate = Pick<Schemas['PollOptionView'], 'label' | 'detail' | 'href' | 'thumbnailUrl'>;

export function NominationCandidate({
  candidate,
  nomination,
  ref,
}: {
  candidate: Candidate;
  nomination: Schemas['VotedNomination'];
  ref?: Ref<HTMLDivElement>;
}) {
  const gallery = nomination === 'funniestMoment';
  const link = candidate.href ? (
    <Link
      to={candidate.href}
      className={cx(standaloneLink, 'justify-self-start font-bold text-action-strong')}
    >
      {gallery ? ru.polls.openGallery : candidate.label}
    </Link>
  ) : null;
  return (
    <div ref={ref} className="grid min-w-0 gap-2 wrap-anywhere">
      {gallery || !link ? <p className="font-bold">{candidate.label}</p> : link}
      {candidate.thumbnailUrl ? (
        <img
          src={candidate.thumbnailUrl}
          alt=""
          loading="lazy"
          className="aspect-video max-h-40 w-full rounded-md bg-page object-contain"
        />
      ) : null}
      {candidate.detail ? <p className="text-sm whitespace-pre-wrap">{candidate.detail}</p> : null}
      {gallery ? link : null}
    </div>
  );
}
