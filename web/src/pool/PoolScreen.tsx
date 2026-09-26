import { Library, Plus, SearchX, SlidersHorizontal } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { watchPool, watchSeason } from '../api/realtime';
import { usePageHeading } from '../app/router';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { Checkbox, ChoiceGroup, Field, Select } from '../ui/Field';
import { Skeleton } from '../ui/Progress';
import { EmptyState, ErrorState, Notice } from '../ui/States';
import { useDesk } from '../ui/useDesk';
import { AddGameForm } from './AddGameForm';
import { PoolGameCard } from './PoolGameCard';
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

type PoolResult = { games: PoolGame[]; categories: Schemas['CategoryView'][] } | 'signedOut' | null;

async function fetchPool(): Promise<PoolResult> {
  try {
    const [games, categories] = await Promise.all([
      api.GET('/api/pool'),
      api.GET('/api/pool/categories'),
    ]);
    if (games.response.status === 401 || categories.response.status === 401) return 'signedOut';
    return games.data && categories.data
      ? { games: games.data, categories: categories.data }
      : null;
  } catch {
    return null;
  }
}

async function fetchStatuses(seasonId: string): Promise<SeasonGame[] | null> {
  try {
    const { data } = await api.GET('/api/seasons/{seasonId}/games', {
      params: { path: { seasonId } },
    });
    return data ?? null;
  } catch {
    return null;
  }
}

/** Cards shown at first and added by «Показать ещё» */
export const pageSize = 60;

/** How long the page waits after a season update before it asks for the statuses again */
export const statusDelayMs = 300;

type Loaded =
  | { kind: 'loading' }
  | { kind: 'failed' }
  | { kind: 'ready'; games: PoolGame[]; categories: Schemas['CategoryView'][] };

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
  const [loaded, setLoaded] = useState<Loaded>({ kind: 'loading' });
  const [statuses, setStatuses] = useState<Map<string, SeasonGame> | null>(null);
  const [statusFailed, setStatusFailed] = useState(false);
  const [filter, setFilter] = useState<PoolFilter>(noFilter);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [adding, setAdding] = useState(false);
  const [added, setAdded] = useState<string | null>(null);
  const [retrying, setRetrying] = useState(false);
  const [showing, setShowing] = useState({ key: '', count: pageSize });
  const desk = useDesk();
  const heading = usePageHeading();
  // Only the latest answer is shown: an older one that comes late must not undo a newer one
  const poolAsked = useRef(0);
  const statusAsked = useRef(0);

  const applyPool = useCallback(
    (result: PoolResult) => {
      if (result === 'signedOut') onSignedOut();
      else if (result) setLoaded({ kind: 'ready', ...result });
      // A failed refresh keeps what the page shows; only the first load turns into the error
      else setLoaded((now) => (now.kind === 'ready' ? now : { kind: 'failed' }));
    },
    [onSignedOut],
  );

  const applyStatuses = useCallback((result: SeasonGame[] | null) => {
    if (result) setStatuses(new Map(result.map((s) => [s.gameId, s])));
    setStatusFailed(!result);
  }, []);

  const loadPool = useCallback(() => {
    const asked = ++poolAsked.current;
    return fetchPool().then((result) => {
      if (asked === poolAsked.current) applyPool(result);
    });
  }, [applyPool]);
  const loadStatuses = useCallback(() => {
    if (!seasonId) return Promise.resolve();
    const asked = ++statusAsked.current;
    return fetchStatuses(seasonId).then((result) => {
      if (asked === statusAsked.current) applyStatuses(result);
    });
  }, [seasonId, applyStatuses]);

  // Load now, after every change of the pool and after every command of the season (someone rolled, dropped…)
  useEffect(() => {
    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const refreshPool = () => {
      const asked = ++poolAsked.current;
      void fetchPool().then((result) => {
        if (active && asked === poolAsked.current) applyPool(result);
      });
    };
    const refreshStatuses = () => {
      if (!seasonId) return;
      const asked = ++statusAsked.current;
      void fetchStatuses(seasonId).then((result) => {
        if (active && asked === statusAsked.current) applyStatuses(result);
      });
    };
    // Commands of the season come in bursts (a roll, its misses, a move): one read after the burst (D-160)
    const statusesSoon = () => {
      clearTimeout(timer);
      timer = setTimeout(refreshStatuses, statusDelayMs);
    };
    refreshPool();
    refreshStatuses();
    const stopPool = watchPool(refreshPool);
    const stopSeason = seasonId ? watchSeason(seasonId, statusesSoon) : () => undefined;
    return () => {
      active = false;
      clearTimeout(timer);
      stopPool();
      stopSeason();
    };
  }, [seasonId, applyPool, applyStatuses]);

  const games = useMemo(() => (loaded.kind === 'ready' ? loaded.games : []), [loaded]);
  const wheel = useMemo(() => wheelOf(loaded.kind === 'ready' ? loaded.categories : []), [loaded]);
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

  // Hundreds of cards at once slow a phone down: the list grows by a page, a new filter starts from the first page
  const filterKey = JSON.stringify(filter);
  const visible = showing.key === filterKey ? showing.count : pageSize;

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
            {(loaded.kind === 'ready' ? loaded.categories : []).map((c) => (
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
        {loaded.kind === 'loading' ? (
          <PoolSkeleton />
        ) : loaded.kind === 'failed' ? (
          <ErrorState
            level={2}
            title={t.loadErrorTitle}
            text={ru.shell.loadErrorText}
            onRetry={() => {
              if (retrying) return;
              setRetrying(true);
              setLoaded({ kind: 'loading' });
              void Promise.all([loadPool(), loadStatuses()]).finally(() => {
                setRetrying(false);
              });
            }}
          />
        ) : games.length === 0 ? (
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
              {shown.slice(0, visible).map((game) => (
                <PoolGameCard
                  key={game.id}
                  game={game}
                  status={statuses?.get(game.id)}
                  inSeason={Boolean(seasonId) && known}
                  inWheel={onWheel(game, wheel)}
                />
              ))}
            </ul>
            {shown.length > visible ? (
              <Button
                className="justify-self-center"
                onClick={() => {
                  setShowing({ key: filterKey, count: visible + pageSize });
                }}
              >
                {t.more(Math.min(pageSize, shown.length - visible), shown.length - visible)}
              </Button>
            ) : null}
          </>
        )}
      </section>

      {canAdd && loaded.kind === 'ready' ? (
        <FormDialog open={adding} onOpenChange={setAdding} title={t.form.title} wide>
          <AddGameForm
            categories={loaded.categories}
            onSignedOut={onSignedOut}
            onAdded={(game) => {
              setAdding(false);
              setAdded(game.title);
              setLoaded((now) =>
                now.kind === 'ready' && !now.games.some((g) => g.id === game.id)
                  ? {
                      ...now,
                      games: [...now.games, game].sort((a, b) => a.title.localeCompare(b.title)),
                    }
                  : now,
              );
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
      <p className="sr-only">{ru.app.loading}</p>
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
