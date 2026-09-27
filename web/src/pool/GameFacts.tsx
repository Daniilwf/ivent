import { Clock, Trash2, Users } from 'lucide-react';
import { ru } from '../i18n/ru';
import { Chip, Tag } from '../ui/Marks';

/**
 * A game's facts, one way everywhere (D-202): the estimate by HowLongToBeat, the year, co-op, deleted, then its
 * categories. The pool's cards, the game page, the admin's pool and the current game show the same block.
 */
export function GameFacts({
  game,
}: {
  game: {
    hours: number | null;
    tags: readonly string[];
    year?: number | null;
    isCoop?: boolean;
    isDeleted?: boolean;
  };
}) {
  const t = ru.gamePage;
  return (
    <div className="grid justify-items-start gap-2" data-testid="game-facts">
      <div className="flex flex-wrap gap-2 tabular-nums">
        <Chip icon={<Clock size={16} aria-hidden />}>{ru.hours.estimate(game.hours)}</Chip>
        {game.year ? <Chip>{t.year(game.year)}</Chip> : null}
        {game.isCoop ? <Chip icon={<Users size={16} aria-hidden />}>{t.coop}</Chip> : null}
        {game.isDeleted ? <Chip icon={<Trash2 size={16} aria-hidden />}>{t.deleted}</Chip> : null}
      </div>
      {game.tags.length > 0 ? (
        <ul className="flex flex-wrap gap-1" aria-label={t.tags}>
          {game.tags.map((tag) => (
            <li key={tag}>
              <Tag>{tag}</Tag>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
