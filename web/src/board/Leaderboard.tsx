import { playerToken } from '../design/players';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';
import { Badge } from '../ui/Marks';
import { ScoreBar } from '../ui/Progress';
import { Sticker } from '../ui/Sticker';
import type { Player } from './types';

const t = ru.board;

/** The leaderboard: the first finisher on top, the rest by points; each row with a score bar against the leader */
export function Leaderboard({
  players,
  cellsLeft,
  limit,
}: {
  players: Player[];
  cellsLeft: (player: Player) => number;
  limit?: number;
}) {
  const top = Math.max(...players.map((p) => p.points), 1);
  return (
    <ol className="grid gap-1">
      {(limit ? players.slice(0, limit) : players).map((p, i) => (
        <li
          key={p.id}
          className={cx(
            'grid grid-cols-[auto_auto_minmax(0,1fr)_auto] items-center gap-3 rounded-md px-2 py-2',
            p.me && 'bg-page outline-2 outline-me',
          )}
          aria-label={ru.leaderboard.row(i + 1, p.name, p.points, p.inactive ? null : cellsLeft(p))}
        >
          <span className="w-6 font-display font-heavy" aria-hidden>
            {i + 1}
          </span>
          <Sticker player={p} size={36} />
          <span className="min-w-0 font-medium" aria-hidden>
            <span className="flex min-w-0 items-center gap-2">
              <span className="truncate" title={p.name}>
                {p.name}
              </span>
              {p.first ? <Badge>{t.first}</Badge> : null}
              {p.me ? <Badge tone="me">{t.you}</Badge> : null}
            </span>
            <span className="block text-xs font-regular text-ink-soft">
              {p.inactive ? t.inactive : ru.ui.toFinish(cellsLeft(p))}
            </span>
            <ScoreBar
              points={p.points}
              top={top}
              fill={p.me ? 'var(--color-me)' : playerToken(p.token).fill}
            />
          </span>
          <span className="font-display text-lg font-heavy" aria-hidden>
            {p.points}
          </span>
        </li>
      ))}
    </ol>
  );
}
