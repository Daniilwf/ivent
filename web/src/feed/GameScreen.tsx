import { CheckCheck, Gamepad2, Play, SearchX, X, RotateCcw, Ban } from 'lucide-react';
import { useCallback, useEffect, useRef, useState, type ReactNode, type Ref } from 'react';
import { api, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { watchSeason } from '../api/realtime';
import { Link } from '../app/Link';
import { paths, usePageHeading } from '../app/router';
import { moscowDay } from '../app/time';
import { Cover } from '../board/GameCards';
import { GameFacts } from '../pool/GameFacts';
import { NotFound } from '../app/NotFound';
import { ru } from '../i18n/ru';
import { standaloneLink } from '../ui/buttonStyles';
import { Badge, Tag } from '../ui/Marks';
import { AsyncState } from '../ui/AsyncState';
import { cx } from '../ui/cx';
import { Skeleton } from '../ui/Progress';
import { EmptyState } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { Panel } from '../ui/Surface';
import { CommentThread } from './CommentThread';
import { Quote } from './Review';
import { ReviewReactionButtons, type ChangeReviewReaction } from './ReviewReactionButtons';
import { useLoaded, type Answer } from '../app/useLoaded';

type Game = Schemas['PoolGameView'];
type Run = Schemas['GameRunView'];
type RunsRead = { request: AbortController; done: Promise<boolean> };

const refreshEvents = new Set([
  'review-reaction-set',
  'run-reviewed',
  'ruleset-changed',
  'command-undone',
]);
const refreshDelayMs = 300;

const statusIcons: Record<string, ReactNode> = {
  playing: <Play size={14} aria-hidden />,
  completed: <CheckCheck size={14} aria-hidden />,
  dropped: <X size={14} aria-hidden />,
  techRerolled: <RotateCcw size={14} aria-hidden />,
  rejected: <Ban size={14} aria-hidden />,
};

/** A game's page (H5, SPEC «Страница игры»): the pool's card and every run of it in every season, with the reviews */
export function GameScreen({ gameId, onSignedOut }: { gameId: string; onSignedOut: () => void }) {
  const load = useCallback(async (): Promise<Answer<{ game: Game; runs: Run[] }>> => {
    try {
      const params = { params: { path: { gameId } } };
      const [card, runs] = await Promise.all([
        api.GET('/api/pool/{gameId}', params),
        api.GET('/api/pool/{gameId}/runs', params),
      ]);
      if (card.data && runs.data)
        return { kind: 'ready', value: { game: card.data, runs: [...runs.data] } };
      if (card.response.status === 401 || runs.response.status === 401)
        return { kind: 'signedOut' };
      if (card.response.status === 404 || runs.response.status === 404) return { kind: 'notFound' };
      return { kind: 'failed' };
    } catch {
      return { kind: 'failed' };
    }
  }, [gameId]);
  const state = useLoaded(load, { onSignedOut });
  const heading = usePageHeading(state.kind === 'ready');

  return (
    <main className="mx-auto grid max-w-180 content-start gap-4 px-4 pt-4 pb-10 desk:px-8 desk:pt-8">
      <AsyncState
        loaded={state}
        skeleton={<GameSkeleton />}
        level={1}
        errorTitle={ru.gamePage.errorTitle}
        notFound={
          <NotFound
            icon={<SearchX size={28} aria-hidden />}
            title={ru.gamePage.notFoundTitle}
            text={ru.gamePage.notFoundText}
          />
        }
      >
        {({ game, runs }) => (
          <GameWithReviewReactions
            key={game.id}
            game={game}
            initialRuns={runs}
            headingRef={heading}
            onSignedOut={onSignedOut}
          />
        )}
      </AsyncState>
    </main>
  );
}

function GameWithReviewReactions({
  game,
  initialRuns,
  headingRef,
  onSignedOut,
}: {
  game: Game;
  initialRuns: Run[];
  headingRef: Ref<HTMLHeadingElement>;
  onSignedOut: () => void;
}) {
  const [{ runs, staleRuns }, setSnapshot] = useState(() => ({
    runs: initialRuns,
    staleRuns: new Set<string>(),
  }));
  const lifetime = useRef<AbortController | null>(null);
  const read = useRef<RunsRead | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    lifetime.current = controller;
    return () => {
      controller.abort();
      read.current?.request.abort();
      read.current = null;
    };
  }, []);

  const gameId = game.id;
  const refreshRuns = useCallback(() => {
    const request = new AbortController();
    const load = async () => {
      try {
        const { data, response } = await api.GET('/api/pool/{gameId}/runs', {
          params: { path: { gameId } },
          signal: request.signal,
        });
        if (request.signal.aborted) return false;
        if (data) {
          setSnapshot({ runs: [...data], staleRuns: new Set() });
          return true;
        }
        if (response.status === 401) {
          onSignedOut();
          return true;
        }
        return false;
      } catch {
        return false;
      }
    };
    // A live refresh may replace this read. A write waiting for its snapshot needs the result of the replacement.
    const done = load().then((ok) =>
      read.current?.request === request ? ok : (read.current?.done ?? true),
    );
    const previous = read.current;
    read.current = { request, done };
    previous?.request.abort();
    return done;
  }, [gameId, onSignedOut]);

  // A game can have reviews in different seasons. Each enabled season is watched once, even with several reviews.
  const seasonKey = [
    ...new Set(
      runs
        .filter((run) => run.reviewReactions?.length || run.canComment)
        .map((run) => run.seasonId),
    ),
  ]
    .sort()
    .join(',');
  useEffect(() => {
    if (!seasonKey) return;
    let queued: ReturnType<typeof setTimeout> | undefined;
    const stops = seasonKey.split(',').map((seasonId) =>
      watchSeason(seasonId, (updates) => {
        if (
          updates.length > 0 &&
          !updates.some((u) => u.types.some((type) => refreshEvents.has(type)))
        )
          return;
        if (queued !== undefined) return;
        queued = setTimeout(() => {
          queued = undefined;
          void refreshRuns();
        }, refreshDelayMs);
      }),
    );
    return () => {
      stops.forEach((stop) => {
        stop();
      });
      clearTimeout(queued);
    };
  }, [seasonKey, refreshRuns]);

  const changeReaction: ChangeReviewReaction = async (runId, emoji, selected) => {
    const signal = lifetime.current?.signal;
    if (!signal) return null;
    const aborted = () => signal.aborted;
    if (aborted()) return null;
    try {
      const { response } = await api.PUT('/api/pool/{gameId}/runs/{runId}/review-reactions', {
        params: { path: { gameId, runId } },
        body: { commandId: newCommandId(), emoji, selected },
        signal,
      });
      if (aborted()) return null;
      if (response.status === 401) {
        onSignedOut();
        return null;
      }
      if (response.status === 429) return 'tooOften';
      if (!response.ok) return 'refused';

      // Apply the acknowledged selection once. The next snapshot supplies the authoritative totals.
      setSnapshot((previous) => ({
        ...previous,
        runs: previous.runs.map((run) =>
          run.runId === runId && run.reviewReactions
            ? {
                ...run,
                reviewReactions: run.reviewReactions.map((reaction) =>
                  reaction.emoji === emoji && reaction.selected !== selected
                    ? { ...reaction, count: reaction.count + (selected ? 1 : -1), selected }
                    : reaction,
                ),
              }
            : run,
        ),
      }));
      if (!(await refreshRuns()) && !aborted()) {
        setSnapshot((previous) => ({
          ...previous,
          staleRuns: new Set([...previous.staleRuns, runId]),
        }));
      }
      return null;
    } catch {
      return aborted() ? null : 'refused';
    }
  };

  return (
    <GameDetails
      game={game}
      runs={runs}
      headingRef={headingRef}
      changeReaction={changeReaction}
      staleRuns={staleRuns}
      onSignedOut={onSignedOut}
    />
  );
}

/** A loaded game page: also what the styleguide shows */
export function GameDetails({
  game,
  runs,
  headingRef,
  changeReaction,
  staleRuns,
  onSignedOut,
}: {
  game: Game;
  runs: Run[];
  /** The page's heading, for the focus when the page is opened (usePageHeading) */
  headingRef?: Ref<HTMLHeadingElement>;
  changeReaction?: ChangeReviewReaction;
  staleRuns?: ReadonlySet<string>;
  onSignedOut?: () => void;
}) {
  return (
    <>
      <header className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-4" data-testid="game">
        {/* The pool's cover, as on the pool page and the roll's cards */}
        <Cover game={{ title: game.title, cover: game.cover?.thumbnailUrl }} width={88} />
        <div className="grid min-w-0 justify-items-start gap-2">
          <h1
            ref={headingRef}
            tabIndex={-1}
            className="font-display text-lg font-heavy text-balance wrap-anywhere outline-none desk:text-xl"
          >
            {game.title}
          </h1>
          <GameFacts game={game} />
        </div>
      </header>
      {game.completionCondition ? (
        <p className="max-w-prose text-ink-soft wrap-anywhere">
          {ru.gamePage.condition(game.completionCondition)}
        </p>
      ) : null}

      <Panel title={ru.gamePage.runsTitle(runs.length)}>
        {runs.length === 0 ? (
          <EmptyState
            icon={<Gamepad2 size={28} aria-hidden />}
            title={ru.gamePage.noRunsTitle}
            text={ru.gamePage.noRunsText}
          />
        ) : (
          <ul className="grid divide-y-2 divide-muted" data-testid="game-runs">
            {runs.map((run) => (
              <li
                key={run.runId}
                className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-3 py-3"
              >
                <Sticker player={{ name: run.playerName, token: run.token }} size={40} />
                <div className="grid min-w-0 gap-2">
                  <div className="flex flex-wrap items-baseline justify-between gap-x-3">
                    <Link
                      to={paths.profile(run.userId)}
                      className={cx(standaloneLink, 'font-bold wrap-anywhere')}
                    >
                      {run.playerName}
                    </Link>
                    <span className="text-sm text-ink-soft tabular-nums">
                      {run.seasonName}
                      {run.completedAt ? `, ${moscowDay(run.completedAt)}` : ''}
                    </span>
                  </div>
                  <ul className="flex flex-wrap items-center gap-1">
                    <li>
                      <Badge tone="muted" icon={statusIcons[run.status]}>
                        {ru.gamePage.status[run.status] ?? run.status}
                      </Badge>
                    </li>
                    {run.difficulty ? (
                      <li>
                        <Tag>{ru.difficulty[run.difficulty]}</Tag>
                      </li>
                    ) : null}
                    {run.hours === null ? null : (
                      <li>
                        <Tag>{ru.gamePage.played(run.hours)}</Tag>
                      </li>
                    )}
                  </ul>
                  <Quote rating={run.rating} text={run.reviewText} />
                  {changeReaction && run.reviewReactions?.length ? (
                    <ReviewReactionButtons
                      runId={run.runId}
                      reactions={run.reviewReactions}
                      changeReaction={changeReaction}
                      stale={staleRuns?.has(run.runId) === true}
                    />
                  ) : null}
                  {run.canComment && onSignedOut ? (
                    <CommentThread
                      target={{
                        kind: 'review',
                        seasonId: run.seasonId,
                        gameId: game.id,
                        runId: run.runId,
                      }}
                      label={ru.comments.reviewLabel(run.playerName)}
                      onSignedOut={onSignedOut}
                    />
                  ) : null}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </>
  );
}

function GameSkeleton() {
  return (
    <div className="grid gap-4" aria-busy="true" data-testid="game-loading">
      <p className="sr-only">{ru.ui.loading}</p>
      <div className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-4">
        <Skeleton className="h-32 w-24 rounded-md" />
        <div className="grid gap-2">
          <Skeleton className="h-8 w-3/4" />
          <Skeleton className="h-7 w-40 rounded-full" />
        </div>
      </div>
      <Skeleton className="h-64 w-full rounded-lg" />
    </div>
  );
}
