import { Gamepad2 } from 'lucide-react';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { cx } from '../ui/cx';
import { ConfirmDanger } from '../ui/Dialogs';
import { Tag } from '../ui/Marks';
import { RouteProgress } from '../ui/Progress';

const t = ru.board;

export type GameCard = {
  title: string;
  tags: string[];
  hours: number | null;
  /** A tall cover (600×900 from Steam) */
  cover?: string | undefined;
};

/** A game's cover, tilted like a card on the table; without a cover, a plain card with a controller */
export function Cover({
  game,
  width = 88,
  className,
}: {
  game: Pick<GameCard, 'title' | 'cover'>;
  width?: number;
  className?: string;
}) {
  const style = { width, height: width * 1.5 };
  const frame = cx('shrink-0 -rotate-3 rounded-sm border-2 border-ink', className);
  return game.cover ? (
    <img
      src={game.cover}
      alt=""
      width={width}
      height={width * 1.5}
      className={cx(frame, 'object-cover')}
      style={style}
    />
  ) : (
    <span
      className={cx(frame, 'grid place-items-center bg-table text-on-color')}
      style={style}
      role="img"
      aria-label={t.noCover}
    >
      <Gamepad2 size={width / 2.5} aria-hidden />
    </span>
  );
}

/** The run in progress: what I play, how far to the finish, the one main action and a quiet drop apart from it */
export function RunCard({
  game,
  left,
  total,
  dropConsequences,
  onComplete,
  onDrop,
  busy = false,
}: {
  game: GameCard;
  /** Cells left to the finish and the whole way's length */
  left: number;
  total: number;
  dropConsequences: readonly string[];
  onComplete?: () => void;
  onDrop?: () => void;
  busy?: boolean;
}) {
  return (
    <section
      className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-3 rounded-lg bg-card p-4"
      aria-label={t.nowPlaying}
    >
      <Cover game={game} />
      <div className="grid content-start gap-1">
        <p className="text-sm text-ink-soft">{t.nowPlaying}</p>
        <h2 className="font-display text-lg font-heavy wrap-anywhere" title={game.title}>
          {game.title}
        </h2>
        <p className="text-sm text-ink-soft">{t.hours(game.hours)}</p>
        <div className="flex flex-wrap gap-1">
          {game.tags.map((tag) => (
            <Tag key={tag}>{tag}</Tag>
          ))}
        </div>
      </div>
      <div className="col-span-2">
        <RouteProgress left={left} total={total} />
      </div>
      <div className="col-span-2 flex flex-wrap items-center justify-between gap-x-5 gap-y-1">
        <Button
          variant="main"
          className="flex-1 whitespace-nowrap"
          loading={busy}
          {...(onComplete ? { onClick: onComplete } : {})}
        >
          {t.complete}
        </Button>
        <ConfirmDanger
          trigger={
            <Button variant="dangerLink" className="ml-auto">
              {t.drop}
            </Button>
          }
          title={t.dropTitle(game.title)}
          consequences={dropConsequences}
          confirm={t.dropConfirm}
          onConfirm={() => onDrop?.()}
        />
      </div>
    </section>
  );
}
