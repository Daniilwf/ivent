import { BookOpen } from 'lucide-react';
import { paths } from '../app/router';
import { ru } from '../i18n/ru';
import { inlineLink } from '../ui/buttonStyles';
import { cx } from '../ui/cx';

/**
 * A link to the rules page's section «Что считается прохождением» (D-207; its anchor is the section's id,
 * `rules-completion`) next to a completion or a proof: the player's card and the admin's proof queue. It opens in a new
 * tab: both places hold a half-filled form that a page change would lose.
 */
export function CompletionRulesLink({ className }: { className?: string }) {
  return (
    <a
      href={`${paths.rules()}#rules-completion`}
      target="_blank"
      rel="noopener"
      className={cx(inlineLink, 'inline-flex min-h-11 items-center gap-2 text-sm', className)}
    >
      <BookOpen size={16} aria-hidden className="shrink-0" />
      {ru.rules.sections.completion}
    </a>
  );
}
