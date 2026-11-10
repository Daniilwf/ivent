import { Flag, Trophy } from 'lucide-react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';

/**
 * The player's own finish (D-99, D-260): the first is provisional until the admin approves the proofs, then frozen and
 * playing on without counting; a later finisher's token stays on the finish while his points still grow. The first is
 * the leaderboard's (`isFirst`): the order of finishing is never reused, so after a revoked finish the first may have
 * finished second; the frozen one is always the first. A later finisher's place is the leaderboard's, by points.
 */
export function MyFinish({
  finish,
  first: leads,
  place,
  rollsOpen,
}: {
  finish: Schemas['MyFinishView'];
  /** The leaderboard says I hold the first place */
  first: boolean;
  /** My place in the leaderboard, if it lists me */
  place: number | null;
  /** Rolls are still open: past the deadline the card does not invite to play on */
  rollsOpen: boolean;
}) {
  const t = ru.turn.myFinish;
  const first = leads || finish.frozen;
  const [main, next] = first
    ? finish.frozen
      ? [t.firstFrozen, t.firstFrozenNext]
      : [t.firstProvisional, t.firstProvisionalNext]
    : [t.later(place), t.laterNext];
  return (
    <section
      data-testid="my-finish"
      aria-labelledby="my-finish-title"
      className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-x-3 gap-y-1 rounded-md bg-muted p-3"
    >
      <span
        aria-hidden
        className={cx(
          'row-span-3 grid size-10 place-items-center rounded-full border-2 border-ink',
          first ? 'bg-gold' : 'bg-card',
        )}
      >
        {first ? <Trophy size={20} /> : <Flag size={20} />}
      </span>
      <h3 id="my-finish-title" className="font-display font-heavy">
        {t.title}
      </h3>
      <p>{main}</p>
      <p className="text-sm text-ink-soft">{rollsOpen ? next : t.closedNext}</p>
    </section>
  );
}
