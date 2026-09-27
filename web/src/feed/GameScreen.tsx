import { CheckCheck, Gamepad2, Play, SearchX, X, RotateCcw, Ban } from 'lucide-react';
import { useCallback, type ReactNode, type Ref } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { paths, usePageHeading } from '../app/router';
import { moscowDay } from '../app/time';
import { Cover } from '../board/GameCards';
import { GameFacts } from '../pool/GameFacts';
import { NotFound } from '../app/NotFound';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { textLink as linkText } from './linkStyle';
import { cx } from '../ui/cx';
import { Skeleton } from '../ui/Progress';
import { EmptyState } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { Panel } from '../ui/Surface';
import { Quote } from './Review';
import { useLoaded, type Answer } from '../app/useLoaded';

type Game = Schemas['PoolGameView'];
type Run = Schemas['GameRunView'];

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
        {({ game, runs }) => <GameDetails game={game} runs={runs} headingRef={heading} />}
      </AsyncState>
    </main>
  );
}

/** A loaded game page: also what the styleguide shows */
export function GameDetails({
  game,
  runs,
  headingRef,
}: {
  game: Game;
  runs: Run[];
  /** The page's heading, for the focus when the page is opened (usePageHeading) */
  headingRef?: Ref<HTMLHeadingElement>;
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
        <p className="max-w-prose text-ink-soft">
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
                      className={cx(linkText, 'font-bold wrap-anywhere')}
                    >
                      {run.playerName}
                    </Link>
                    <span className="text-sm text-ink-soft tabular-nums">
                      {run.seasonName}
                      {run.completedAt ? `, ${moscowDay(run.completedAt)}` : ''}
                    </span>
                  </div>
                  <ul className="flex flex-wrap gap-1 text-sm">
                    <li className="inline-flex items-center gap-1 rounded-full bg-muted px-2 font-medium">
                      {statusIcons[run.status]}
                      {ru.gamePage.status[run.status] ?? run.status}
                    </li>
                    {run.difficulty ? (
                      <li className="rounded-full bg-muted px-2">
                        {ru.difficulty[run.difficulty]}
                      </li>
                    ) : null}
                    {run.hours === null ? null : (
                      <li className="rounded-full bg-muted px-2 tabular-nums">
                        {ru.gamePage.played(run.hours)}
                      </li>
                    )}
                  </ul>
                  <Quote rating={run.rating} text={run.reviewText} />
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
