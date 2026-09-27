import { useCallback, useMemo } from 'react';
import { Link } from '../app/Link';
import { paths } from '../app/router';
import { useLoaded } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { cx } from '../ui/cx';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { FeedList, FeedSkeleton } from './FeedList';
import { emptyRefs, feedDays, withRefs } from './feedModel';
import { fetchFeedPage } from './feedApi';

/** How many lines the season screen shows */
const PREVIEW_LINES = 5;

/**
 * The latest lines of the feed beside the map on a desktop (DESIGN.md «Главная»). `version` — the season's last log
 * sequence: when it grows, the lines are read again (the season screen already listens to the server).
 */
export function FeedPreview({
  seasonId,
  version,
  className,
}: {
  seasonId: string;
  version: number;
  className?: string;
}) {
  // A failed refresh keeps the lines shown
  const state = useLoaded(
    useCallback(() => fetchFeedPage(seasonId, null, 30), [seasonId]),
    { version },
  );

  const days = useMemo(() => {
    if (state.kind !== 'ready') return [];
    const all = feedDays(state.value.entries, withRefs(emptyRefs(), state.value));
    // The newest lines only, their days kept
    let left = PREVIEW_LINES;
    return all.flatMap((day) => {
      if (left <= 0) return [];
      const items = day.items.slice(0, left);
      left -= items.length;
      return [{ ...day, items }];
    });
  }, [state]);

  return (
    <Panel title={ru.feed.preview} className={cx(className)} data-testid="feed-preview">
      {state.kind === 'loading' ? (
        <FeedSkeleton rows={3} />
      ) : state.kind !== 'ready' ? (
        <div className="grid justify-items-start gap-2">
          <Notice tone="danger">{ru.feed.errorTitle}</Notice>
          <Button onClick={state.reload}>{ru.ui.retry}</Button>
        </div>
      ) : days.length === 0 ? (
        <p className="text-ink-soft">{ru.feed.emptyTitle}</p>
      ) : (
        <FeedList days={days} level={3} />
      )}
      <Link
        to={paths.feed()}
        className="inline-flex min-h-11 items-center justify-self-start rounded-sm px-1 font-bold underline underline-offset-4 is-hover:decoration-2"
      >
        {ru.feed.all}
      </Link>
    </Panel>
  );
}
