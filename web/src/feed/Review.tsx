import { Star } from 'lucide-react';
import { ru } from '../i18n/ru';
import { Badge } from '../ui/Marks';

/** A rating out of 10: a star and the number, said in words for screen readers */
export function Rating({ value }: { value: number }) {
  return (
    <span className="justify-self-start">
      <Badge
        tone="gold"
        label={ru.feed.ratingLabel(value)}
        icon={<Star size={14} aria-hidden className="fill-ink" />}
      >
        <span aria-hidden>{ru.feed.rating(value)}</span>
      </Badge>
    </span>
  );
}

/** A review or a comment under a line: the rating (if any) and the words as the player wrote them */
export function Quote({ rating, text }: { rating: number | null; text: string | null }) {
  // A rating alone is a mark, not a quote
  if (!text) return rating === null ? null : <Rating value={rating} />;
  return (
    <blockquote className="grid justify-items-start gap-1 border-l-3 border-muted pl-3">
      {rating === null ? null : <Rating value={rating} />}
      <p className="max-w-prose whitespace-pre-line wrap-anywhere text-ink">{text}</p>
    </blockquote>
  );
}
