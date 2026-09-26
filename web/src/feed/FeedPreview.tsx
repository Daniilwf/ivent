import { useEffect, useMemo, useState } from 'react';
import type { Schemas } from '../api/client';
import { Link } from '../app/Link';
import { paths } from '../app/router';
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

type State =
  { kind: 'loading' } | { kind: 'failed' } | { kind: 'ready'; page: Schemas['FeedView'] };

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
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let active = true;
    void fetchFeedPage(seasonId, null, 30).then((result) => {
      if (!active) return;
      if (result.kind === 'page') setState({ kind: 'ready', page: result.page });
      // A failed refresh keeps the lines shown
      else setState((shown) => (shown.kind === 'ready' ? shown : { kind: 'failed' }));
    });
    return () => {
      active = false;
    };
  }, [seasonId, version, attempt]);

  const days = useMemo(() => {
    if (state.kind !== 'ready') return [];
    const all = feedDays(state.page.entries, withRefs(emptyRefs(), state.page));
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
      ) : state.kind === 'failed' ? (
        <div className="grid justify-items-start gap-2">
          <Notice tone="danger">{ru.feed.errorTitle}</Notice>
          <Button
            onClick={() => {
              setState({ kind: 'loading' });
              setAttempt((n) => n + 1);
            }}
          >
            {ru.ui.retry}
          </Button>
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
