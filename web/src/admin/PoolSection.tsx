import { Dices, Library, Plus } from 'lucide-react';
import { useCallback, useDeferredValue, useState, type SyntheticEvent } from 'react';
import { api, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ConfirmDanger } from '../ui/Dialogs';
import { Checkbox, Field } from '../ui/Field';
import { EmptyState, Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { refusal } from './actions';
import { newCommandId } from '../api/commands';
import { AsyncState } from '../ui/AsyncState';
import { GameFacts } from '../pool/GameFacts';
import { GameForm } from '../pool/GameForm';
import { usePaging } from '../pool/usePaging';
import { answerOf, useLoaded } from '../app/useLoaded';

const t = ru.admin.pool;

type Game = Schemas['PoolGameView'];
type Message = { tone: 'success' | 'danger'; text: string } | null;

/** A category weight typed by the admin: a whole number 1–1000 */
function parseWeight(text: string): number | null {
  const clean = text.trim();
  if (!/^\d+$/.test(clean)) return null;
  const weight = Number(clean);
  return weight >= 1 && weight <= 1000 ? weight : null;
}

/** The category wheel with weights and available games, and the pool's games */
export function PoolSection({ seasonId }: { seasonId: string | null }) {
  const [message, setMessage] = useState<Message>(null);
  return (
    <div className="grid gap-6" data-testid="admin-pool">
      {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
      <Categories seasonId={seasonId} onMessage={setMessage} />
      <Games onMessage={setMessage} />
    </div>
  );
}

function Categories({
  seasonId,
  onMessage,
}: {
  seasonId: string | null;
  onMessage: (message: Message) => void;
}) {
  const loaded = useLoaded(
    useCallback(async () => {
      const [categories, stats] = await Promise.all([
        api.GET('/api/pool/categories'),
        seasonId
          ? api.GET('/api/admin/seasons/{seasonId}/pool-stats', { params: { path: { seasonId } } })
          : Promise.resolve(null),
      ]);
      if (!categories.data) return answerOf(categories);
      return {
        kind: 'ready' as const,
        value: { categories: categories.data, stats: stats?.data ?? null },
      };
    }, [seasonId]),
  );

  return (
    <Panel title={t.categories} data-testid="admin-categories">
      <p className="max-w-prose text-ink-soft">{t.categoriesLead}</p>
      <AsyncState loaded={loaded} rows={2} errorTitle={ru.admin.loadErrorTitle}>
        {({ categories, stats }, reload) => {
          const done = (text: string) => {
            onMessage({ tone: 'success', text });
            reload();
          };
          const failed = (text: string) => {
            onMessage({ tone: 'danger', text });
          };
          const available = new Map(stats?.categories.map((c) => [c.category, c.available]));
          const starving = stats?.playersWithoutGames ?? [];
          return (
            <>
              {starving.length > 0 ? (
                <Notice tone="warning">{t.starving(starving.map((p) => p.name).join(', '))}</Notice>
              ) : null}
              {categories.length === 0 ? (
                <EmptyState
                  icon={<Dices size={28} aria-hidden />}
                  title={t.categoriesEmptyTitle}
                  text={t.categoriesEmptyText}
                />
              ) : (
                <ul className="grid gap-2">
                  {categories.map((c) => (
                    <li key={c.name}>
                      <CategoryRow
                        category={c}
                        available={available.get(c.name) ?? null}
                        onDone={done}
                        onFailed={failed}
                      />
                    </li>
                  ))}
                </ul>
              )}
              <AddCategory onDone={done} onFailed={failed} />
            </>
          );
        }}
      </AsyncState>
    </Panel>
  );
}

function CategoryRow({
  category,
  available,
  onDone,
  onFailed,
}: {
  category: Schemas['CategoryView'];
  available: number | null;
  onDone: (text: string) => void;
  onFailed: (text: string) => void;
}) {
  const [weight, setWeight] = useState(String(category.weight));
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<'save' | 'remove' | null>(null);
  const [confirming, setConfirming] = useState(false);

  async function save(e: SyntheticEvent) {
    e.preventDefault();
    const value = parseWeight(weight);
    if (value === null) {
      setError(t.weightInvalid);
      return;
    }
    setError(undefined);
    setBusy('save');
    try {
      const answer = await api.PUT('/api/admin/pool/categories/{name}', {
        params: { path: { name: category.name } },
        body: { commandId: newCommandId(), weight: value },
      });
      if (answer.data) onDone(t.weightSaved(category.name));
      else onFailed(refusal(answer));
    } catch {
      onFailed(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  async function remove() {
    setBusy('remove');
    try {
      const answer = await api.POST('/api/admin/pool/categories/{name}/remove', {
        params: { path: { name: category.name } },
        body: { commandId: newCommandId() },
      });
      setConfirming(false);
      if (answer.data) onDone(t.removed(category.name));
      else onFailed(refusal(answer));
    } catch {
      setConfirming(false);
      onFailed(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  return (
    <form
      className="grid gap-2 rounded-md bg-page p-3 desk:grid-cols-[minmax(0,1fr)_auto] desk:items-end"
      data-testid={`category-${category.name}`}
      onSubmit={(e) => void save(e)}
      noValidate
    >
      <div className="grid gap-1">
        <p className="font-bold break-all">{category.name}</p>
        <p className="text-sm text-ink-soft">
          {t.inPool(category.games)}
          {available === null ? null : (
            <>
              {', '}
              <span className={available === 0 ? 'font-bold text-danger' : undefined}>
                {available === 0 ? t.noneAvailable : t.available(available)}
              </span>
            </>
          )}
        </p>
      </div>
      <div className="flex flex-wrap items-end gap-3">
        <Field
          label={t.categoryWeight}
          aria-label={t.weight(category.name)}
          inputMode="numeric"
          value={weight}
          error={error}
          className="w-28"
          data-testid="category-weight"
          onChange={(e) => {
            setWeight(e.target.value);
          }}
        />
        <Button
          type="submit"
          loading={busy === 'save'}
          disabled={busy === 'remove' || weight === String(category.weight)}
          data-testid="category-save"
        >
          {ru.admin.save}
        </Button>
        <ConfirmDanger
          open={confirming}
          onOpenChange={setConfirming}
          trigger={
            <Button variant="dangerLink" data-testid="category-remove">
              {t.remove}
            </Button>
          }
          title={t.removeTitle(category.name)}
          consequences={t.removeConsequences}
          confirm={t.removeConfirm}
          busy={busy === 'remove'}
          onConfirm={() => void remove()}
        />
      </div>
    </form>
  );
}

function AddCategory({
  onDone,
  onFailed,
}: {
  onDone: (text: string) => void;
  onFailed: (text: string) => void;
}) {
  const [name, setName] = useState('');
  const [weight, setWeight] = useState('1');
  const [errors, setErrors] = useState<{ name?: string | undefined; weight?: string | undefined }>(
    {},
  );
  const [busy, setBusy] = useState(false);

  async function submit(e: SyntheticEvent) {
    e.preventDefault();
    const value = parseWeight(weight);
    const found = {
      name: name.trim() === '' ? t.tagRequired : undefined,
      weight: value === null ? t.weightInvalid : undefined,
    };
    setErrors(found);
    if (found.name || value === null) return;
    setBusy(true);
    try {
      const answer = await api.PUT('/api/admin/pool/categories/{name}', {
        params: { path: { name: name.trim() } },
        body: { commandId: newCommandId(), weight: value },
      });
      if (answer.data) {
        setName('');
        setWeight('1');
        onDone(t.weightSaved(name.trim()));
      } else onFailed(refusal(answer));
    } catch {
      onFailed(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="grid gap-3 desk:grid-cols-[minmax(0,1fr)_auto_auto] desk:items-end"
      onSubmit={(e) => void submit(e)}
      aria-label={t.addCategory}
      noValidate
    >
      <Field
        label={t.categoryTag}
        hint={t.categoryTagHint}
        value={name}
        maxLength={50}
        error={errors.name}
        data-testid="category-new-name"
        onChange={(e) => {
          setName(e.target.value);
        }}
      />
      <Field
        label={t.categoryWeight}
        inputMode="numeric"
        value={weight}
        error={errors.weight}
        className="desk:w-28"
        data-testid="category-new-weight"
        onChange={(e) => {
          setWeight(e.target.value);
        }}
      />
      <div>
        <Button
          type="submit"
          icon={<Plus size={20} aria-hidden />}
          loading={busy}
          data-testid="category-add"
        >
          {t.addCategory}
        </Button>
      </div>
    </form>
  );
}

function Games({ onMessage }: { onMessage: (message: Message) => void }) {
  const [query, setQuery] = useState('');
  const [deleted, setDeleted] = useState(false);
  const search = useDeferredValue(query.trim());
  const categories = useLoaded(
    useCallback(async () => answerOf(await api.GET('/api/pool/categories')), []),
  );
  const loaded = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/pool', {
            params: { query: search === '' ? { deleted } : { query: search, deleted } },
          }),
        ),
      [search, deleted],
    ),
  );

  return (
    <Panel title={t.games} data-testid="admin-games">
      <div className="grid gap-3 desk:grid-cols-[minmax(0,1fr)_auto] desk:items-end">
        <Field
          type="search"
          label={ru.pool.search}
          value={query}
          data-testid="games-search"
          onChange={(e) => {
            setQuery(e.target.value);
          }}
        />
        <Checkbox
          label={t.showDeleted}
          checked={deleted}
          data-testid="games-deleted"
          onChange={(e) => {
            setDeleted(e.target.checked);
          }}
        />
      </div>
      <AsyncState loaded={loaded} rows={3} errorTitle={ru.admin.loadErrorTitle}>
        {(games, reload) => (
          <GameList
            games={games}
            searched={search !== ''}
            listKey={`${search}|${String(deleted)}`}
            categories={categories.kind === 'ready' ? categories.value : []}
            onMessage={onMessage}
            reload={reload}
          />
        )}
      </AsyncState>
    </Panel>
  );
}

/** The pool's games in the admin: a count, the list page by page like the pool page's, each with its actions */
function GameList({
  games,
  searched,
  listKey,
  categories,
  onMessage,
  reload,
}: {
  games: Game[];
  searched: boolean;
  listKey: string;
  categories: Schemas['CategoryView'][];
  onMessage: (message: Message) => void;
  reload: () => void;
}) {
  const { page, more } = usePaging(games, listKey);
  return games.length === 0 ? (
    <EmptyState
      icon={<Library size={28} aria-hidden />}
      title={searched ? ru.pool.nothingTitle : ru.pool.emptyTitle}
      text={searched ? ru.pool.nothingText : t.gamesEmptyText}
    />
  ) : (
    <div className="grid gap-3">
      <p className="text-ink-soft tabular-nums" aria-live="polite" data-testid="games-count">
        {searched ? ru.pool.searched(games.length) : ru.pool.found(games.length, games.length)}
      </p>
      <ul className="grid gap-2">
        {page.map((game) => (
          <li key={game.id}>
            <GameRow
              game={game}
              categories={categories}
              onDone={(text) => {
                onMessage({ tone: 'success', text });
                reload();
              }}
              onFailed={(text) => {
                onMessage({ tone: 'danger', text });
              }}
            />
          </li>
        ))}
      </ul>
      {more}
    </div>
  );
}

function GameRow({
  game,
  categories,
  onDone,
  onFailed,
}: {
  game: Game;
  categories: Schemas['CategoryView'][];
  onDone: (text: string) => void;
  onFailed: (text: string) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);

  async function act(action: 'delete' | 'restore') {
    setBusy(true);
    try {
      const params = { path: { gameId: game.id } };
      const body = { commandId: newCommandId() };
      const answer =
        action === 'delete'
          ? await api.POST('/api/admin/pool/{gameId}/delete', { params, body })
          : await api.POST('/api/admin/pool/{gameId}/restore', { params, body });
      setConfirming(false);
      if (answer.data)
        onDone(action === 'delete' ? t.deletedOk(game.title) : t.restored(game.title));
      else onFailed(refusal(answer));
    } catch {
      setConfirming(false);
      onFailed(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <article
      className="grid gap-2 rounded-md bg-page p-3 wrap-anywhere"
      data-testid={`game-${game.id}`}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <p className="mr-auto font-bold">{game.title}</p>
      </div>
      <GameFacts game={game} />
      <div className="flex flex-wrap items-center gap-3">
        {game.isDeleted ? (
          <Button loading={busy} data-testid="game-restore" onClick={() => void act('restore')}>
            {t.restore}
          </Button>
        ) : (
          <>
            <Button
              variant="link"
              aria-expanded={editing}
              data-testid="game-edit"
              onClick={() => {
                setEditing(!editing);
              }}
            >
              {ru.admin.edit}
            </Button>
            <ConfirmDanger
              open={confirming}
              onOpenChange={setConfirming}
              trigger={
                <Button variant="dangerLink" data-testid="game-delete">
                  {t.delete}
                </Button>
              }
              title={t.deleteTitle(game.title)}
              consequences={t.deleteConsequences}
              confirm={t.deleteConfirm}
              busy={busy}
              onConfirm={() => void act('delete')}
            />
          </>
        )}
      </div>
      {editing ? (
        <GameForm
          categories={categories}
          game={game}
          onSaved={(saved) => {
            setEditing(false);
            onDone(t.saved(saved.title));
          }}
          onSignedOut={() => {
            onFailed(ru.admin.forbidden);
          }}
        />
      ) : null}
    </article>
  );
}
