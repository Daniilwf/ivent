import { MessagesSquare, SearchX } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Schemas } from '../api/client';
import { watchSeason } from '../api/realtime';
import { navigate, paths, usePageHeading } from '../app/router';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { EmptyState, ErrorState, Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { FeedList, FeedSkeleton } from './FeedList';
import { emptyRefs, feedDays, mergeEntries, withRefs, type FeedRefs } from './feedModel';
import { fetchFeedPage } from './feedApi';

type Entry = Schemas['FeedEntryView'];

type Feed = { entries: Entry[]; refs: FeedRefs; nextBefore: number | null };
type State =
  { kind: 'loading' } | { kind: 'failed' } | { kind: 'notFound' } | ({ kind: 'ready' } & Feed);

/** The season's feed (H5, SPEC «Лента»): everything that happened, as lines, by day, newest first, page by page */
export function FeedScreen({
  seasonId,
  onSignedOut,
}: {
  seasonId: string;
  onSignedOut: () => void;
}) {
  const [state, setState] = useState<State>({ kind: 'loading' });
  const heading = usePageHeading();
  const [more, setMore] = useState<'idle' | 'loading' | 'failed'>('idle');
  const [fresh, setFresh] = useState<ReadonlySet<string>>(new Set());
  const [announced, setAnnounced] = useState('');
  // What is shown, for the callbacks that run after an await: they build on the latest, not on their render's
  const current = useRef<State>({ kind: 'loading' });
  const show = useCallback((next: State) => {
    current.current = next;
    setState(next);
  }, []);

  const load = useCallback(async () => {
    const result = await fetchFeedPage(seasonId, null);
    if (result.kind === 'signedOut') {
      onSignedOut();
      return;
    }
    const before = current.current;
    if (result.kind !== 'page') {
      // A failed refresh keeps what is shown; only the first load shows the error
      if (before.kind !== 'ready') show({ kind: result.kind });
      return;
    }
    const { page } = result;
    if (before.kind !== 'ready') {
      show({
        kind: 'ready',
        entries: page.entries,
        refs: withRefs(emptyRefs(), page),
        nextBefore: page.nextBefore,
      });
      return;
    }
    // A refresh: the newest page over what is shown; the lines of commands not seen before arrive softly
    const newest = before.entries[0]?.sequence ?? 0;
    const known = new Set(before.entries.map((e) => e.commandId));
    const arrived = new Set(
      page.entries
        .filter((e) => e.sequence > newest && !known.has(e.commandId))
        .map((e) => e.commandId),
    );
    show({
      ...before,
      entries: mergeEntries(before.entries, page.entries),
      refs: withRefs(before.refs, page),
    });
    if (arrived.size) {
      setFresh(arrived);
      setAnnounced(ru.feed.fresh(arrived.size));
    }
  }, [seasonId, onSignedOut, show]);

  useEffect(() => {
    void load();
    // Others' actions come in without a reload (DESIGN.md «Обновления в реальном времени»)
    return watchSeason(seasonId, () => void load());
  }, [seasonId, load, show]);

  async function loadMore() {
    const shown = current.current;
    if (shown.kind !== 'ready' || shown.nextBefore === null) return;
    setMore('loading');
    const result = await fetchFeedPage(seasonId, shown.nextBefore);
    if (result.kind === 'signedOut') {
      onSignedOut();
      return;
    }
    if (result.kind !== 'page') {
      setMore('failed');
      return;
    }
    const latest = current.current;
    if (latest.kind === 'ready') {
      show({
        ...latest,
        entries: mergeEntries(latest.entries, result.page.entries),
        refs: withRefs(latest.refs, result.page),
        nextBefore: result.page.nextBefore,
      });
    }
    setMore('idle');
  }

  const days = useMemo(
    () => (state.kind === 'ready' ? feedDays(state.entries, state.refs) : []),
    [state],
  );

  return (
    <main className="mx-auto grid max-w-180 content-start gap-4 px-4 pt-4 pb-10 desk:px-8 desk:pt-8">
      <h1 ref={heading} tabIndex={-1} className="font-display text-xl font-heavy outline-none">
        {ru.feed.title}
      </h1>
      <p className="sr-only" aria-live="polite" data-testid="feed-announce">
        {announced}
      </p>
      {state.kind === 'loading' ? (
        <Panel>
          <FeedSkeleton />
        </Panel>
      ) : state.kind === 'failed' ? (
        <ErrorState
          level={2}
          title={ru.feed.errorTitle}
          text={ru.feed.errorText}
          onRetry={() => {
            show({ kind: 'loading' });
            void load();
          }}
        />
      ) : state.kind === 'notFound' ? (
        <EmptyState
          level={2}
          icon={<SearchX size={28} aria-hidden />}
          title={ru.feed.noSeasonTitle}
          text={ru.feed.noSeasonText}
          action={
            <Button
              variant="main"
              onClick={() => {
                navigate(paths.feed());
              }}
            >
              {ru.nav.feed}
            </Button>
          }
        />
      ) : days.length === 0 && state.nextBefore === null ? (
        <EmptyState
          level={2}
          icon={<MessagesSquare size={28} aria-hidden />}
          title={ru.feed.emptyTitle}
          text={ru.feed.emptyText}
          action={
            <Button
              variant="main"
              onClick={() => {
                navigate(paths.season());
              }}
            >
              {ru.feed.emptyAction}
            </Button>
          }
        />
      ) : (
        <Panel>
          <FeedList days={days} fresh={fresh} />
          <div className="grid justify-items-center gap-3 pt-2">
            {more === 'failed' ? <Notice tone="danger">{ru.feed.moreError}</Notice> : null}
            {state.nextBefore === null ? (
              <p className="text-sm text-ink-soft" data-testid="feed-start">
                {ru.feed.start}
              </p>
            ) : (
              <Button
                data-testid="feed-more"
                loading={more === 'loading'}
                onClick={() => void loadMore()}
              >
                {ru.feed.more}
              </Button>
            )}
          </div>
        </Panel>
      )}
    </main>
  );
}
