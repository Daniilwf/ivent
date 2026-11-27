import { MessagesSquare, SearchX } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Schemas } from '../api/client';
import { navigate, paths, useEntryState, usePageHeading, useRestoredScroll } from '../app/router';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { PageHeader } from '../ui/PageHeader';
import { Button } from '../ui/Button';
import { EmptyState, ErrorState, Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { CommentThread } from './CommentThread';
import { isCommentEvent } from './commentApi';
import { FeedList, FeedSkeleton } from './FeedList';
import { emptyRefs, feedDays, mergeEntries, withRefs, type FeedRefs } from './feedModel';
import {
  FEED_PAGE,
  FEED_REACTION_BATCH,
  fetchFeedPage,
  fetchFeedReactions,
  setFeedReaction,
  type FeedReactionMap,
} from './feedApi';
import { ReactionButtons, type ChangeReaction } from './ReviewReactionButtons';
import { useRollDisclosureRefresh } from './useRollDisclosureRefresh';

type Entry = Schemas['FeedEntryView'];
type ReactionRow = { values: FeedReactionMap[string]; revision: number; stale: boolean };
type FeedReactions = { enabledAt: number; rows: Map<string, ReactionRow> };

/** `pages`: how many pages are shown */
type Feed = {
  entries: Entry[];
  refs: FeedRefs;
  nextBefore: number | null;
  pages: number;
  reactions: FeedReactions | null;
  /** Visible counts, with null for a checked row that has no thread. */
  commentTargets: ReadonlyMap<string, number | null> | null;
};
type State =
  { kind: 'loading' } | { kind: 'failed' } | { kind: 'notFound' } | ({ kind: 'ready' } & Feed);

// A live refresh can use both feed reads, which share the season's 60-per-minute budget.
const liveRefreshIntervalMs = 3000;
const commandIdsOf = (entries: readonly Entry[]) => [...new Set(entries.map((e) => e.commandId))];

function rollRevealId({ data }: Entry) {
  return data !== null &&
    typeof data === 'object' &&
    'revealId' in data &&
    typeof data.revealId === 'string'
    ? data.revealId
    : null;
}

function mergeCommentTargets(
  previous: Feed['commentTargets'],
  pageIds: readonly string[],
  allowed: readonly string[],
  counts: Schemas['FeedView']['commentCounts'],
): ReadonlyMap<string, number | null> {
  const next = new Map(previous);
  const eligible = new Set(allowed);
  for (const id of pageIds) next.set(id, eligible.has(id) ? (counts?.[id] ?? 0) : null);
  return next;
}

function markUndone(entries: Entry[], page: readonly Entry[]): Entry[] {
  const targets = new Set<string>();
  for (const { type, data } of page) {
    if (
      type === 'command-undone' &&
      data !== null &&
      typeof data === 'object' &&
      'commandId' in data &&
      typeof data.commandId === 'string'
    ) {
      targets.add(data.commandId);
    }
  }
  // The newest page carries the undo summary even when its target is on an older page.
  return targets.size
    ? entries.map((entry) =>
        !entry.undone && targets.has(entry.commandId) ? { ...entry, undone: true } : entry,
      )
    : entries;
}

function reconcileReactions(
  previous: FeedReactions | null,
  requested: readonly string[],
  result: FeedReactionMap,
  revision: number,
): FeedReactions | null {
  if (previous === null || revision < previous.enabledAt) return previous;
  const rows = new Map(previous.rows);
  for (const id of requested) {
    // A page or live read may have started before the last acknowledged write to this row.
    if ((rows.get(id)?.revision ?? 0) > revision) continue;
    // The server omits ineligible rows. Keep their revision so older answers cannot restore them.
    rows.set(id, { values: result[id] ?? [], revision, stale: false });
  }
  return { ...previous, rows };
}

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
  // The pages shown stay in the history entry: Back from a profile loads them again and scrolls back (K1A)
  const [pages, setPages] = useEntryState('feed.pages', 1);
  const [fresh, setFresh] = useState<ReadonlySet<string>>(new Set());
  const [announced, setAnnounced] = useState('');
  // What is shown, for the callbacks that run after an await: they build on the latest, not on their render's
  const current = useRef<State>({ kind: 'loading' });
  const lifetime = useRef<AbortController | null>(null);
  const newestPage = useRef<AbortController | null>(null);
  const changedComments = useRef<{ through: number } | null>(null);
  const revision = useRef(0);
  const headRevision = useRef(0);
  useEffect(() => {
    const controller = new AbortController();
    lifetime.current = controller;
    return () => {
      controller.abort();
      newestPage.current?.abort();
    };
  }, []);

  const show = useCallback((next: State) => {
    current.current = next;
    setState(next);
  }, []);

  const refreshReactions = useCallback(
    async (ids: string[], signal: AbortSignal) => {
      let complete = true;
      // Read one bounded batch at a time so scrolling farther back does not fan out a burst of requests.
      for (let i = 0; i < ids.length; i += FEED_REACTION_BATCH) {
        const targets = ids.slice(i, i + FEED_REACTION_BATCH);
        const started = ++revision.current;
        const result = await fetchFeedReactions(seasonId, targets, signal);
        if (signal.aborted) return false;
        if (result.kind === 'signedOut') {
          onSignedOut();
          return true;
        }
        const latest = current.current;
        if (latest.kind !== 'ready' || latest.reactions === null) return true;
        if (result.kind === 'failed' && result.tooOften) return false;
        if (result.kind !== 'ready') {
          complete = false;
          continue;
        }
        show({
          ...latest,
          reactions: reconcileReactions(latest.reactions, targets, result.value, started),
        });
      }
      return complete;
    },
    [seasonId, onSignedOut, show],
  );

  const refreshCommentTargets = useCallback(
    async (before: number, oldest: number, signal: AbortSignal) => {
      let cursor: number | null = before;
      // Read through the affected pages already shown, a page at a time.
      while (cursor !== null && cursor > oldest) {
        const result = await fetchFeedPage(seasonId, cursor, FEED_PAGE, signal);
        if (signal.aborted) return false;
        if (result.kind === 'signedOut') {
          onSignedOut();
          return false;
        }
        const latest = current.current;
        if (
          result.kind !== 'ready' ||
          result.value.commentTargets == null ||
          latest.kind !== 'ready' ||
          latest.commentTargets === null
        )
          return false;
        const page = result.value;
        show({
          ...latest,
          commentTargets: mergeCommentTargets(
            latest.commentTargets,
            commandIdsOf(page.entries),
            result.value.commentTargets,
            page.commentCounts,
          ),
        });
        cursor = page.nextBefore;
      }
      return true;
    },
    [seasonId, onSignedOut, show],
  );

  const refreshHiddenRolls = useCallback(
    async (entries: readonly Entry[], signal: AbortSignal) => {
      for (const entry of entries) {
        // A receipt or elapsed delay only asks the server again. The server still decides when to name the game.
        const updated = await fetchFeedPage(seasonId, entry.sequence + 1, 1, signal);
        if (signal.aborted) return;
        if (updated.kind === 'signedOut') {
          onSignedOut();
          return;
        }
        const latest = current.current;
        if (updated.kind !== 'ready' || latest.kind !== 'ready') continue;
        const roll = updated.value.entries.find(
          (candidate) => candidate.sequence === entry.sequence,
        );
        const previous = latest.entries.find((candidate) => candidate.sequence === entry.sequence);
        if (roll && previous?.type === 'game-roll-hidden') {
          show({
            ...latest,
            entries: mergeEntries(latest.entries, [
              previous.undone ? { ...roll, undone: true } : roll,
            ]),
            refs: withRefs(latest.refs, updated.value),
          });
        }
      }
    },
    [seasonId, onSignedOut, show],
  );
  useRollDisclosureRefresh(state.kind === 'ready' ? state.entries : null, (due) => {
    const signal = lifetime.current?.signal;
    if (signal) void refreshHiddenRolls(due, signal);
  });

  const load = useCallback(async () => {
    newestPage.current?.abort();
    const request = new AbortController();
    newestPage.current = request;
    const started = ++revision.current;
    const result = await fetchFeedPage(seasonId, null, FEED_PAGE, request.signal);
    if (request.signal.aborted) return;
    if (result.kind === 'signedOut') {
      onSignedOut();
      return;
    }
    const before = current.current;
    if (result.kind !== 'ready') {
      // A failed refresh keeps what is shown; only the first load shows the error
      if (before.kind !== 'ready') show({ kind: result.kind });
      return;
    }
    const page = result.value;
    headRevision.current = started;
    const pageIds = commandIdsOf(page.entries);
    const commentTargets =
      page.commentTargets == null
        ? null
        : mergeCommentTargets(
            before.kind === 'ready' ? before.commentTargets : null,
            pageIds,
            page.commentTargets,
            page.commentCounts,
          );
    const previousReactions = before.kind === 'ready' ? before.reactions : null;
    const reactions =
      page.reactions == null
        ? null
        : reconcileReactions(
            previousReactions ?? { enabledAt: started, rows: new Map() },
            pageIds,
            page.reactions,
            started,
          );
    if (before.kind !== 'ready') {
      show({
        kind: 'ready',
        entries: page.entries,
        refs: withRefs(emptyRefs(), page),
        nextBefore: page.nextBefore,
        pages: 1,
        reactions,
        commentTargets,
      });
      return;
    }
    // A refresh: the newest page over what is shown; the lines of commands not seen before arrive softly
    const newest = before.entries[0]?.sequence ?? 0;
    const known = new Set(before.entries.map((e) => e.commandId));
    const arrived = new Set(
      page.entries
        .filter(
          (e) => e.type !== 'roll-game-shown' && e.sequence > newest && !known.has(e.commandId),
        )
        .map((e) => e.commandId),
    );
    const entries = markUndone(mergeEntries(before.entries, page.entries), page.entries);
    show({
      ...before,
      entries,
      refs: withRefs(before.refs, page),
      reactions,
      commentTargets,
    });
    if (arrived.size) {
      setFresh(arrived);
      setAnnounced(ru.feed.fresh(arrived.size));
    }
    const revealed = new Set(
      page.entries
        .filter((entry) => entry.type === 'roll-game-shown' && !entry.undone)
        .map(rollRevealId)
        .filter((id) => id !== null),
    );
    if (revealed.size) {
      const hidden = entries.filter((entry) => {
        const id = rollRevealId(entry);
        return entry.type === 'game-roll-hidden' && id !== null && revealed.has(id);
      });
      void refreshHiddenRolls(hidden, request.signal);
    }
    const changed = changedComments.current;
    if (!commentTargets || page.nextBefore === null) {
      changedComments.current = null;
    } else {
      const oldestUnchecked = entries.findLast(
        (entry) => !entry.undone && !commentTargets.has(entry.commandId),
      );
      const through = Math.min(oldestUnchecked?.sequence ?? Infinity, changed?.through ?? Infinity);
      if (through !== Infinity) {
        void refreshCommentTargets(page.nextBefore, through, request.signal).then((complete) => {
          if (complete && changedComments.current === changed) changedComments.current = null;
        });
      }
    }
    if (reactions !== null) {
      const pageCommands = new Set(pageIds);
      const olderIds = commandIdsOf(entries.filter((entry) => !entry.undone)).filter(
        (id) => !pageCommands.has(id),
      );
      void refreshReactions(olderIds, request.signal);
    }
  }, [seasonId, onSignedOut, refreshCommentTargets, refreshHiddenRolls, refreshReactions, show]);

  // Others' actions come in without a reload (DESIGN.md «Обновления в реальном времени»)
  const version = useSeasonVersion(seasonId, {
    refreshOnJoin: true,
    throttleMs: liveRefreshIntervalMs,
    relevant: ({ types, commentTarget }) => {
      const feedChanged = types.some((type) => !isCommentEvent(type));
      const latest = current.current;
      if (
        latest.kind !== 'ready' ||
        !latest.commentTargets ||
        commentTarget?.kind === 'review' ||
        !types.some((type) => isCommentEvent(type) && type !== 'comment-edited')
      )
        return feedChanged;
      const id = commentTarget?.id?.toLowerCase();
      if (id && latest.commentTargets.get(id) == null) return feedChanged;
      const changed = id
        ? latest.entries.find((entry) => entry.commandId === id)
        : latest.entries.at(-1);
      if (!changed) return feedChanged;
      changedComments.current = {
        through: Math.min(changedComments.current?.through ?? Infinity, changed.sequence),
      };
      return true;
    },
  });
  useEffect(() => {
    void load();
  }, [load, version]);

  async function loadMore() {
    const shown = current.current;
    const signal = lifetime.current?.signal;
    if (shown.kind !== 'ready' || shown.nextBefore === null || !signal) return;
    setMore('loading');
    for (let attempt = 0; attempt < 2; attempt++) {
      const headAtStart = headRevision.current;
      const started = ++revision.current;
      const result = await fetchFeedPage(seasonId, shown.nextBefore, FEED_PAGE, signal);
      if (signal.aborted) return;
      if (result.kind === 'signedOut') {
        onSignedOut();
        return;
      }
      if (result.kind !== 'ready') break;
      // A live refresh may contain an undo of a command on this still-hidden page.
      if (headAtStart !== headRevision.current) continue;
      const latest = current.current;
      if (latest.kind === 'ready') {
        const page = result.value;
        const pageIds = commandIdsOf(page.entries);
        show({
          ...latest,
          entries: mergeEntries(latest.entries, page.entries),
          refs: withRefs(latest.refs, page),
          nextBefore: page.nextBefore,
          pages: latest.pages + 1,
          reactions:
            page.reactions == null
              ? latest.reactions
              : reconcileReactions(latest.reactions, pageIds, page.reactions, started),
          commentTargets:
            latest.commentTargets && page.commentTargets != null
              ? mergeCommentTargets(
                  latest.commentTargets,
                  pageIds,
                  page.commentTargets,
                  page.commentCounts,
                )
              : latest.commentTargets,
        });
        if (latest.reactions && page.reactions == null) void refreshReactions(pageIds, signal);
      }
      setMore('idle');
      return;
    }
    setMore('failed');
  }

  const changeReaction: ChangeReaction = async (id, emoji, selected) => {
    const signal = lifetime.current?.signal;
    if (!signal) return null;
    const aborted = () => signal.aborted;
    if (aborted()) return null;
    const outcome = await setFeedReaction(seasonId, id, emoji, selected, signal);
    if (aborted()) return null;
    if (outcome === 'signedOut') {
      onSignedOut();
      return null;
    }
    if (outcome !== 'saved') return outcome;

    const latest = current.current;
    if (latest.kind !== 'ready' || latest.reactions === null) return null;
    const row = latest.reactions.rows.get(id);
    if (!row?.values.length) return null;
    const saved = ++revision.current;
    const values = row.values.map((reaction) =>
      reaction.emoji === emoji && reaction.selected !== selected
        ? { ...reaction, count: reaction.count + (selected ? 1 : -1), selected }
        : reaction,
    );
    show({
      ...latest,
      reactions: reconcileReactions(latest.reactions, [id], { [id]: values }, saved),
    });

    if (!(await refreshReactions([id], signal)) && !aborted()) {
      const shown = current.current;
      if (shown.kind === 'ready' && shown.reactions?.rows.get(id)?.revision === saved) {
        const rows = new Map(shown.reactions.rows);
        rows.set(id, { values, revision: saved, stale: true });
        show({ ...shown, reactions: { ...shown.reactions, rows } });
      }
    }
    return null;
  };

  // Back to the feed: the older pages shown before come again, one after another
  const restoring = state.kind === 'ready' && state.nextBefore !== null && state.pages < pages;
  useEffect(() => {
    if (restoring && more === 'idle') void loadMore();
  });
  useRestoredScroll(state.kind === 'ready' && !restoring);

  const entries = state.kind === 'ready' ? state.entries : null;
  const refs = state.kind === 'ready' ? state.refs : null;
  const days = useMemo(() => (entries && refs ? feedDays(entries, refs) : []), [entries, refs]);

  return (
    <main className="mx-auto grid max-w-180 content-start gap-4 px-4 pt-4 pb-10 desk:px-8 desk:pt-8">
      <PageHeader ref={heading} tabIndex={-1} className="outline-none">
        {ru.feed.title}
      </PageHeader>
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
          text={ru.shell.loadErrorText}
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
          <FeedList
            days={days}
            fresh={fresh}
            renderReactions={(id) => {
              const row = state.reactions?.rows.get(id);
              return row?.values.length ? (
                <ReactionButtons
                  targetId={id}
                  groupLabel={ru.feed.reactionGroup}
                  reactions={row.values}
                  changeReaction={changeReaction}
                  stale={row.stale}
                  compact
                />
              ) : null;
            }}
            renderComments={(item) => {
              const count = state.commentTargets?.get(item.id);
              if (count == null) return null;
              const title = item.line
                .map((part) =>
                  typeof part === 'string'
                    ? part
                    : part.kind === 'player'
                      ? part.player.name
                      : part.game.title,
                )
                .join('');
              return (
                <CommentThread
                  target={{ kind: 'feed', seasonId, targetCommandId: item.id }}
                  label={ru.comments.feedLabel(title)}
                  onSignedOut={onSignedOut}
                  count={count}
                />
              );
            }}
          />
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
                onClick={() => {
                  setPages(state.pages + 1);
                  void loadMore();
                }}
              >
                {ru.ui.showMore}
              </Button>
            )}
          </div>
        </Panel>
      )}
    </main>
  );
}
