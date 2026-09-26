import { Star } from 'lucide-react';
import { ru } from '../i18n/ru';

/** A rating out of 10: a star and the number, said in words for screen readers */
export function Rating({ value }: { value: number }) {
  return (
    <span
      className="inline-flex shrink-0 items-center gap-1 rounded-full bg-gold px-2 text-sm font-bold text-ink"
      aria-label={ru.feed.ratingLabel(value)}
      role="img"
    >
      <Star size={14} aria-hidden className="fill-ink" />
      <span aria-hidden>{ru.feed.rating(value)}</span>
    </span>
  );
}

/** A review or a comment under a line: the rating (if any) and the words as the player wrote them */
export function Quote({ rating, text }: { rating: number | null; text: string | null }) {
  if (rating === null && !text) return null;
  return (
    <blockquote className="grid justify-items-start gap-1 border-l-3 border-muted pl-3">
      {rating === null ? null : <Rating value={rating} />}
      {text ? (
        <p className="max-w-prose whitespace-pre-line wrap-anywhere text-ink">{text}</p>
      ) : null}
    </blockquote>
  );
}
