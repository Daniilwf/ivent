import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';
import { Badge } from '../ui/Marks';
import { ScoreBar } from '../ui/Progress';
import { Sticker } from '../ui/Sticker';
import type { Player } from './types';

/** A leaderboard row as the server orders it (D-100): the first finisher on top, the rest by points */
export type LeaderRow = {
  player: Player;
  place: number;
  points: number;
  /** null: no way to the finish from where the token stands (API `cellsToFinish`) */
  cellsToFinish: number | null;
  isFirst: boolean;
  provisional: boolean;
};

const t = ru.board;

/** The leaderboard with score bars against the leader; each row is also one sentence for screen readers */
export function Leaderboard({
  rows,
  limit,
  marked = true,
}: {
  rows: LeaderRow[];
  limit?: number;
  /** The test ids (`leaderboard`, `leader-*`): on one copy only when a page shows the leaderboard twice */
  marked?: boolean;
}) {
  const top = Math.max(...rows.map((r) => r.points), 1);
  return (
    <ol className="grid gap-1" data-testid={marked ? 'leaderboard' : undefined}>
      {(limit ? rows.slice(0, limit) : rows).map((row) => {
        const p = row.player;
        return (
          <li
            key={p.id}
            data-testid={marked ? `leader-${p.id}` : undefined}
            data-me={p.me ? true : undefined}
            className={cx(
              'grid grid-cols-[auto_auto_minmax(0,1fr)_auto] items-center gap-3 rounded-md px-2 py-2',
              p.me && 'bg-page outline-2 outline-me',
            )}
          >
            <span className="sr-only" data-testid="leader-sentence">
              {ru.leaderboard.row(row.place, p.name, row.points, row.cellsToFinish)}
              {row.isFirst
                ? ` ${row.provisional ? ru.leaderboard.provisional : ru.leaderboard.first}`
                : ''}
            </span>
            <span className="w-6 font-display font-heavy" aria-hidden>
              {row.place}
            </span>
            <span aria-hidden>
              <Sticker player={p} size={36} />
            </span>
            <span className="min-w-0 font-medium" aria-hidden>
              <span className="flex min-w-0 items-center gap-2">
                <span className="truncate" title={p.name}>
                  {p.name}
                </span>
                {row.isFirst ? (
                  <Badge>{row.provisional ? t.firstProvisional : t.first}</Badge>
                ) : null}
                {p.me ? <Badge tone="me">{t.you}</Badge> : null}
              </span>
              <span className="block text-xs font-regular text-ink-soft">
                {row.cellsToFinish === null ? t.noWay : ru.ui.toFinish(row.cellsToFinish)}
              </span>
              <ScoreBar
                points={row.points}
                top={top}
                fill={p.me ? 'var(--color-me)' : 'var(--color-ink-soft)'}
              />
            </span>
            <span className="font-display text-lg font-heavy" aria-hidden>
              {row.points}
            </span>
          </li>
        );
      })}
    </ol>
  );
}
