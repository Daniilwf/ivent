import {
  CheckCheck,
  Clock,
  Gamepad2,
  Play,
  SearchX,
  Trash2,
  Users,
  X,
  RotateCcw,
  Ban,
} from 'lucide-react';
import { useCallback, type ReactNode, type Ref } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { navigate, paths, usePageHeading } from '../app/router';
import { moscowDay } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { cx } from '../ui/cx';
import { Chip, Tag } from '../ui/Marks';
import { Skeleton } from '../ui/Progress';
import { EmptyState, ErrorState } from '../ui/States';
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

const linkText = cx(
  'rounded-sm underline decoration-2 decoration-muted underline-offset-4 is-hover:decoration-ink',
);

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
      {state.kind === 'loading' ? (
        <GameSkeleton />
      ) : state.kind === 'failed' ? (
        <ErrorState
          level={1}
          title={ru.gamePage.errorTitle}
          text={ru.shell.loadErrorText}
          onRetry={state.reload}
        />
      ) : state.kind === 'notFound' ? (
        <EmptyState
          level={1}
          icon={<SearchX size={28} aria-hidden />}
          title={ru.gamePage.notFoundTitle}
          text={ru.gamePage.notFoundText}
          action={
            <Button
              variant="main"
              onClick={() => {
                navigate(paths.season());
              }}
            >
              {ru.profile.toSeason}
            </Button>
          }
        />
      ) : (
        <GameDetails game={state.value.game} runs={state.value.runs} headingRef={heading} />
      )}
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
        {game.cover ? (
          <img
            src={game.cover.thumbnailUrl}
            alt=""
            width={96}
            height={128}
            className="h-32 w-24 rounded-md border-3 border-ink bg-muted object-cover"
          />
        ) : (
          <span className="grid h-32 w-24 place-items-center rounded-md border-3 border-ink bg-muted text-ink-soft">
            <Gamepad2 size={32} aria-hidden />
          </span>
        )}
        <div className="grid min-w-0 justify-items-start gap-2">
          <h1
            ref={headingRef}
            tabIndex={-1}
            className="font-display text-lg font-heavy text-balance wrap-anywhere outline-none desk:text-xl"
          >
            {game.title}
          </h1>
          <div className="flex flex-wrap gap-2">
            <Chip icon={<Clock size={16} aria-hidden />}>{ru.gamePage.hours(game.hours)}</Chip>
            {game.year ? <Chip>{ru.gamePage.year(game.year)}</Chip> : null}
            {game.isCoop ? (
              <Chip icon={<Users size={16} aria-hidden />}>{ru.gamePage.coop}</Chip>
            ) : null}
            {game.isDeleted ? (
              <Chip icon={<Trash2 size={16} aria-hidden />}>{ru.gamePage.deleted}</Chip>
            ) : null}
          </div>
          {game.tags.length ? (
            <ul className="flex flex-wrap gap-1" aria-label={ru.gamePage.tags}>
              {game.tags.map((tag) => (
                <li key={tag}>
                  <Tag>{tag}</Tag>
                </li>
              ))}
            </ul>
          ) : null}
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
      <p className="sr-only">{ru.app.loading}</p>
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
