import { Library, Plus, SearchX, SlidersHorizontal } from 'lucide-react';
import { useCallback, useMemo, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { usePageHeading } from '../app/router';
import { answerOf, useLoaded, type Answer } from '../app/useLoaded';
import { usePoolVersion, useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { Checkbox, ChoiceGroup, Field, Select } from '../ui/Field';
import { Skeleton } from '../ui/Progress';
import { AsyncState } from '../ui/AsyncState';
import { EmptyState, Notice } from '../ui/States';
import { useDesk } from '../ui/useDesk';
import { GameForm } from './GameForm';
import { PoolGameCard } from './PoolGameCard';
import { usePaging } from './usePaging';
import {
  activeFilters,
  lengthFilters,
  matches,
  noFilter,
  onWheel,
  wheelOf,
  type LengthFilter,
  type PoolFilter,
  type PoolGame,
  type SeasonGame,
} from './poolFilter';

const t = ru.pool;

type Pool = { games: PoolGame[]; categories: Schemas['CategoryView'][] };

async function fetchPool(): Promise<Answer<Pool>> {
  const [games, categories] = await Promise.all([
    api.GET('/api/pool'),
    api.GET('/api/pool/categories'),
  ]);
  if (!games.data) return answerOf(games);
  if (!categories.data) return answerOf(categories);
  return { kind: 'ready', value: { games: games.data, categories: categories.data } };
}

/** How long the page waits after a season update before it asks for the statuses again */
export const statusDelayMs = 300;

/**
 * The pool of games (H6; SPEC «Пул игр», «Статусы игры в сезоне»): every game with its cover, hours, tags and its status
 * in the current season; search and filters by category, length and «free for me»; a player or the admin adds a game.
 * The pool and the statuses follow the hub: another player's roll or a new game shows up without a reload.
 */
export function PoolScreen({
  seasonId,
  canAdd,
  onSignedOut,
}: {
  seasonId: string | null;
  canAdd: boolean;
  onSignedOut: () => void;
}) {
  const [filter, setFilter] = useState<PoolFilter>(noFilter);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [adding, setAdding] = useState(false);
  const [added, setAdded] = useState<string | null>(null);
  // Games added here show at once, before the pool's own update brings them
  const [addedGames, setAddedGames] = useState<PoolGame[]>([]);
  const desk = useDesk();
  const heading = usePageHeading();

  // The pool follows its own hub; the statuses follow the season, one read after a burst of commands (D-160)
  const loaded = useLoaded(
    useCallback(() => fetchPool(), []),
    {
      onSignedOut,
      version: usePoolVersion(),
    },
  );
  const statusVersion = useSeasonVersion(seasonId, { debounceMs: statusDelayMs });
  const statusLoad = useLoaded(
    useCallback(async (): Promise<Answer<SeasonGame[]>> => {
      if (!seasonId) return { kind: 'ready', value: [] };
      return answerOf(
        await api.GET('/api/seasons/{seasonId}/games', { params: { path: { seasonId } } }),
      );
    }, [seasonId]),
    { version: statusVersion },
  );
  const statuses = useMemo(
    () =>
      statusLoad.kind === 'ready' ? new Map(statusLoad.value.map((s) => [s.gameId, s])) : null,
    [statusLoad],
  );
  const statusFailed = statusLoad.kind === 'failed' || statusLoad.kind === 'notFound';

  const games = useMemo(() => {
    if (loaded.kind !== 'ready') return [];
    const known = new Set(loaded.value.games.map((g) => g.id));
    const extra = addedGames.filter((g) => !known.has(g.id));
    return extra.length === 0
      ? loaded.value.games
      : [...loaded.value.games, ...extra].sort((a, b) => a.title.localeCompare(b.title));
  }, [loaded, addedGames]);
  const wheel = useMemo(
    () => wheelOf(loaded.kind === 'ready' ? loaded.value.categories : []),
    [loaded],
  );
  // «Only free» waits for the statuses: without them it would pass every game
  const known = statuses !== null;
  const shown = useMemo(
    () =>
      games.filter((game) =>
        matches(
          game,
          statuses?.get(game.id),
          { ...filter, freeOnly: filter.freeOnly && known },
          wheel,
        ),
      ),
    [games, statuses, filter, known, wheel],
  );
  const filters = activeFilters(filter);

  // A new filter starts from the first page
  const { page, more } = usePaging(shown, JSON.stringify(filter));

  function change(next: Partial<PoolFilter>) {
    setFilter((now) => ({ ...now, ...next }));
  }

  const addButton = canAdd ? (
    <Button
      variant="main"
      icon={<Plus size={22} aria-hidden />}
      onClick={() => {
        setAdded(null);
        setAdding(true);
      }}
      data-testid="add-game-open"
    >
      {t.add}
    </Button>
  ) : null;

  // The pool as it came: empty, nothing for the filters, or its cards page by page
  function list() {
    return games.length === 0 ? (
      <EmptyState
        level={2}
        icon={<Library size={28} aria-hidden />}
        title={t.emptyTitle}
        text={canAdd ? t.emptyText : t.emptyTextViewer}
      />
    ) : shown.length === 0 ? (
      <EmptyState
        level={2}
        icon={<SearchX size={28} aria-hidden />}
        title={t.nothingTitle}
        text={t.nothingText}
        action={
          <Button
            onClick={() => {
              setFilter(noFilter);
            }}
          >
            {t.reset}
          </Button>
        }
      />
    ) : (
      <>
        <ul className="grid gap-3 desk:grid-cols-2" data-testid="pool-list">
          {page.map((game) => (
            <PoolGameCard
              key={game.id}
              game={game}
              status={statuses?.get(game.id)}
              inSeason={Boolean(seasonId) && known}
              inWheel={onWheel(game, wheel)}
            />
          ))}
        </ul>
        {more}
      </>
    );
  }

  return (
    <main className="mx-auto grid max-w-300 gap-4 px-4 pt-4 pb-10 desk:grid-cols-[auto_minmax(0,1fr)] desk:items-start desk:gap-x-8 desk:gap-y-6 desk:px-8 desk:pt-6">
      <header className="grid gap-3 desk:col-span-2 desk:flex desk:items-end desk:justify-between">
        <div className="grid gap-1">
          <h1
            ref={heading}
            tabIndex={-1}
            className="font-display text-2xl font-heavy outline-none desk:text-3xl"
          >
            {t.title}
          </h1>
          {loaded.kind === 'ready' ? (
            <p className="text-ink-soft tabular-nums" aria-live="polite" data-testid="pool-count">
              {t.found(shown.length, games.length)}
            </p>
          ) : null}
        </div>
        {addButton}
      </header>

      {added ? (
        <div className="desk:col-span-2">
          <Notice tone="success">{t.added(added)}</Notice>
        </div>
      ) : null}

      <div
        role="search"
        aria-label={t.searchRegion}
        className="grid content-start gap-4 desk:sticky desk:top-24 desk:w-72 desk:rounded-lg desk:bg-card desk:p-4"
      >
        <Field
          label={t.search}
          type="search"
          value={filter.query}
          autoComplete="off"
          onChange={(e) => {
            change({ query: e.target.value });
          }}
        />
        {desk ? null : (
          <Button
            icon={<SlidersHorizontal size={20} aria-hidden />}
            aria-expanded={filtersOpen}
            aria-controls="pool-filters"
            className="justify-self-start"
            onClick={() => {
              setFiltersOpen((open) => !open);
            }}
          >
            {t.filters(filters)}
          </Button>
        )}
        <div id="pool-filters" className="grid gap-4" hidden={!desk && !filtersOpen}>
          <Select
            label={t.category}
            value={filter.category}
            onChange={(e) => {
              change({ category: e.target.value });
            }}
          >
            <option value="">{t.anyCategory}</option>
            {(loaded.kind === 'ready' ? loaded.value.categories : []).map((c) => (
              <option key={c.name} value={c.name}>
                {t.categoryOption(c.name, c.games)}
              </option>
            ))}
          </Select>
          <ChoiceGroup<LengthFilter>
            label={t.length}
            value={filter.length}
            options={lengthFilters.map((value) => ({ value, label: t.lengths[value] }))}
            onChange={(length) => {
              change({ length });
            }}
          />
          {seasonId ? (
            <Checkbox
              label={t.freeOnly}
              disabled={!known}
              checked={filter.freeOnly}
              onChange={(e) => {
                change({ freeOnly: e.target.checked });
              }}
            />
          ) : null}
          {filters > 0 || filter.query ? (
            <Button
              variant="link"
              className="justify-self-start"
              onClick={() => {
                setFilter(noFilter);
              }}
            >
              {t.reset}
            </Button>
          ) : null}
        </div>
      </div>

      <section className="grid content-start gap-4" aria-label={t.listLabel}>
        {statusFailed ? <Notice tone="warning">{t.statusError}</Notice> : null}
        <AsyncState
          // A retry asks for the pool and its statuses again
          loaded={{
            ...loaded,
            reload: () => {
              loaded.reload();
              statusLoad.reload();
            },
          }}
          skeleton={<PoolSkeleton />}
          errorTitle={t.loadErrorTitle}
        >
          {() => list()}
        </AsyncState>
      </section>

      {canAdd && loaded.kind === 'ready' ? (
        <FormDialog open={adding} onOpenChange={setAdding} title={t.form.title} wide>
          <GameForm
            categories={loaded.value.categories}
            onSignedOut={onSignedOut}
            onSaved={(game) => {
              setAdding(false);
              setAdded(game.title);
              setAddedGames((now) => [...now, game]);
            }}
          />
        </FormDialog>
      ) : null}
    </main>
  );
}

/** The first load: grey cards of the same size, so nothing jumps when the pool comes */
function PoolSkeleton() {
  return (
    <div className="grid gap-3 desk:grid-cols-2" aria-busy="true" data-testid="pool-loading">
      <p className="sr-only">{ru.ui.loading}</p>
      {[0, 1, 2, 3].map((i) => (
        <div key={i} className="grid grid-cols-[auto_minmax(0,1fr)] gap-4 rounded-lg bg-card p-4">
          <Skeleton className="h-24 w-16" />
          <div className="grid content-start gap-2">
            <Skeleton className="h-6 w-3/4" />
            <Skeleton className="h-4 w-1/3" />
            <Skeleton className="h-7 w-1/2 rounded-full" />
          </div>
        </div>
      ))}
    </div>
  );
}
