import {
  CalendarClock,
  Dices,
  FileCheck,
  Flag,
  MessageSquareQuote,
  Play,
  ShieldCheck,
  Sparkles,
  Trophy,
  Undo2,
  X,
} from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from '../app/Link';
import { paths } from '../app/router';
import { moscowClock, moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { inlineLink } from '../ui/buttonStyles';
import { Badge, Tag } from '../ui/Marks';
import { cx } from '../ui/cx';
import { Skeleton } from '../ui/Progress';
import { Sticker } from '../ui/Sticker';
import type { FeedDay, FeedIcon, FeedItem, FeedRef } from './feedModel';
import { Quote } from './Review';

const icons: Record<FeedIcon, ReactNode> = {
  season: <CalendarClock size={14} aria-hidden />,
  roll: <Dices size={14} aria-hidden />,
  start: <Play size={14} aria-hidden />,
  complete: <Trophy size={14} aria-hidden />,
  review: <MessageSquareQuote size={14} aria-hidden />,
  drop: <X size={14} aria-hidden />,
  proof: <FileCheck size={14} aria-hidden />,
  finish: <Flag size={14} aria-hidden />,
  effect: <Sparkles size={14} aria-hidden />,
  admin: <ShieldCheck size={14} aria-hidden />,
  undo: <Undo2 size={14} aria-hidden />,
};

/** A name in a line: a link to the player's profile or the game's page, plain text when there is no page */
function Ref({ part }: { part: FeedRef }) {
  if (part.kind === 'player') {
    const { player } = part;
    return player.hasProfile ? (
      <Link to={paths.profile(player.userId)} className={cx(inlineLink, 'font-bold')}>
        {player.name}
      </Link>
    ) : (
      <strong>{player.name}</strong>
    );
  }
  const { game } = part;
  return game.hasPage ? (
    <Link to={paths.game(game.id)} className={cx(inlineLink, 'font-medium')}>
      {game.title}
    </Link>
  ) : (
    <span className="font-medium">{game.title}</span>
  );
}

/** Who the line is about: their sticker with the kind of event on its corner; a season's own line has the icon alone */
function Avatar({ item }: { item: FeedItem }) {
  if (!item.actor)
    return (
      <span className="grid size-10 shrink-0 place-items-center rounded-full bg-muted text-ink">
        {icons[item.icon]}
      </span>
    );
  return (
    <span className="relative inline-grid shrink-0">
      <Sticker player={item.actor} size={40} />
      <span className="absolute -right-1 -bottom-1 grid size-5 place-items-center rounded-full border-2 border-card bg-muted text-ink">
        {icons[item.icon]}
      </span>
    </span>
  );
}

/** One line of the feed: one command of the log — who did what, a few facts and a quote */
export function FeedLine({ item, fresh = false }: { item: FeedItem; fresh?: boolean }) {
  return (
    <li
      data-testid={`feed-item-${item.id}`}
      data-undone={item.undone || undefined}
      className={cx(
        'grid grid-cols-[auto_minmax(0,1fr)] items-start gap-3 py-3',
        fresh && 'animate-arrive',
      )}
    >
      <Avatar item={item} />
      <div className="grid min-w-0 gap-2">
        <div className="flex items-start justify-between gap-3">
          <p
            className={cx(
              'min-w-0 wrap-anywhere',
              item.undone && 'text-ink-soft line-through decoration-2',
            )}
          >
            {item.line.map((part, i) =>
              typeof part === 'string' ? part : <Ref key={i} part={part} />,
            )}
          </p>
          <time
            dateTime={item.at}
            title={moscowTime(item.at)}
            className="shrink-0 text-sm text-ink-soft tabular-nums"
          >
            {moscowClock(item.at)}
          </time>
        </div>
        {item.undone ? (
          <p className="justify-self-start">
            <Badge tone="danger" icon={<Undo2 size={14} aria-hidden className="text-danger" />}>
              {ru.feed.undone}
              <span className="sr-only">{ru.feed.undoneHint}</span>
            </Badge>
          </p>
        ) : null}
        {item.facts.length ? (
          <ul className="flex flex-wrap gap-1">
            {item.facts.map((fact, i) => (
              <li key={i}>
                <Tag>{fact}</Tag>
              </li>
            ))}
          </ul>
        ) : null}
        {item.quote ? <Quote rating={item.quote.rating} text={item.quote.text} /> : null}
      </div>
    </li>
  );
}

/** The feed's lines under their days' headings, newest first. `fresh` — lines that just came in, which arrive softly */
export function FeedList({
  days,
  fresh,
  level = 2,
}: {
  days: FeedDay[];
  fresh?: ReadonlySet<string>;
  /** The days' heading level: 2 on the feed's page, 3 inside a panel */
  level?: 2 | 3;
}) {
  const Heading = `h${level}` as const;
  return (
    <div className="grid gap-4" data-testid="feed">
      {days.map((day) => (
        <section key={day.key} aria-labelledby={`feed-day-${day.key}`} className="grid">
          <Heading
            id={`feed-day-${day.key}`}
            className="font-display text-base font-heavy text-ink-soft"
          >
            {day.label}
          </Heading>
          <ol className="grid divide-y-2 divide-muted">
            {day.items.map((item) => (
              <FeedLine key={item.id} item={item} fresh={fresh?.has(item.id) ?? false} />
            ))}
          </ol>
        </section>
      ))}
    </div>
  );
}

/** The feed while it loads: the same rows in grey, so nothing jumps when they come */
export function FeedSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <div className="grid gap-4" aria-busy="true" data-testid="feed-loading">
      <p className="sr-only">{ru.ui.loading}</p>
      <Skeleton className="h-6 w-24" />
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-3">
          <Skeleton className="size-10 rounded-full" />
          <div className="grid gap-2">
            <Skeleton className="h-5 w-4/5" />
            <Skeleton className="h-5 w-1/3" />
          </div>
        </div>
      ))}
    </div>
  );
}
