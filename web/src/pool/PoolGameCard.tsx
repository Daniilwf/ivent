import { Ban, CircleCheck, Gamepad2, Sparkle, Users } from 'lucide-react';
import { moscowDay } from '../app/time';
import { Cover } from '../board/GameCards';
import { ru } from '../i18n/ru';
import { Chip, Tag } from '../ui/Marks';
import type { PoolGame, SeasonGame } from './poolFilter';

const t = ru.pool;

/**
 * A game's status in the season (SPEC «Статусы игры в сезоне»): completed or being played by someone, free, or never
 * coming to me again; other players' drops as marks. Words and an icon carry it, the tint only helps.
 */
export function GameStatus({ status }: { status: SeasonGame | undefined }) {
  const marks = status?.marks ?? [];
  return (
    <span className="grid justify-items-start gap-1" data-testid="game-status">
      {status?.taken === 'completedInSeason' ? (
        <Chip tone="success" icon={<CircleCheck size={16} aria-hidden className="text-success" />}>
          {t.completed(
            status.takenBy ?? '',
            status.completedAt ? moscowDay(status.completedAt) : null,
          )}
        </Chip>
      ) : status?.taken === 'beingPlayed' ? (
        <Chip tone="info" icon={<Gamepad2 size={16} aria-hidden className="text-info" />}>
          {t.playing(status.takenBy ?? '')}
        </Chip>
      ) : status?.excludedForMe ? (
        <Chip icon={<Ban size={16} aria-hidden className="text-ink-soft" />}>
          {t.excluded[status.excludedForMe]}
        </Chip>
      ) : (
        <Chip icon={<Sparkle size={16} aria-hidden className="text-success" />}>{t.free}</Chip>
      )}
      {marks.length > 0 ? (
        <span className="grid gap-1 text-sm text-ink-soft">
          {marks.map((mark, i) => (
            <span key={i}>{ru.turn.gameMark(mark.playerName, mark.kind)}</span>
          ))}
        </span>
      ) : null}
    </span>
  );
}

/** A game of the pool: the cover, the title, its facts and tags, the status in the season and the author's note */
export function PoolGameCard({
  game,
  status,
  inSeason,
}: {
  game: PoolGame;
  status: SeasonGame | undefined;
  /** Without a season there are no statuses to show */
  inSeason: boolean;
}) {
  const note = game.completionCondition ?? game.note;
  return (
    <li
      className="grid grid-cols-[auto_minmax(0,1fr)] content-start gap-x-4 gap-y-3 rounded-lg bg-card p-4"
      data-testid="pool-game"
    >
      <Cover game={{ title: game.title, cover: game.cover?.thumbnailUrl }} width={64} lazy />
      <div className="grid min-w-0 content-start justify-items-start gap-2">
        <h2 className="font-display text-lg font-heavy text-balance wrap-anywhere">{game.title}</h2>
        <p className="flex flex-wrap gap-x-3 gap-y-1 text-sm text-ink-soft tabular-nums">
          <span>{game.hours === null ? t.noHours : t.hours(game.hours)}</span>
          {game.year === null ? null : <span>{game.year}</span>}
          {game.isCoop ? (
            <span className="inline-flex items-center gap-1">
              <Users size={14} aria-hidden />
              {t.coop}
            </span>
          ) : null}
        </p>
        {game.tags.length > 0 ? (
          <span className="flex flex-wrap gap-1">
            {game.tags.map((tag) => (
              <Tag key={tag}>{tag}</Tag>
            ))}
          </span>
        ) : null}
        {inSeason ? <GameStatus status={status} /> : null}
      </div>
      {note || game.author ? (
        <div className="col-span-2 grid gap-1 text-sm">
          {note ? <p className="max-w-prose wrap-anywhere">{note}</p> : null}
          {game.author ? <p className="text-ink-soft">{t.author(game.author)}</p> : null}
        </div>
      ) : null}
    </li>
  );
}
