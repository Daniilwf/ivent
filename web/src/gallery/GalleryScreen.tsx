import { Expand, Images, Plus, SearchX } from 'lucide-react';
import {
  useCallback,
  useEffect,
  useEffectEvent,
  useRef,
  useState,
  type ReactNode,
  type RefObject,
} from 'react';
import { api } from '../api/client';
import { newCommandId } from '../api/commands';
import { navigate, paths, usePageHeading, useRestoredScroll } from '../app/router';
import { moscowDay, moscowTime } from '../app/time';
import { answerOf, useLoaded } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';
import type { ChangeReaction } from '../feed/ReviewReactionButtons';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Select } from '../ui/Field';
import { Skeleton } from '../ui/Progress';
import { EmptyState, ErrorState, Notice } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { GalleryMomentDialog, GalleryThumbnail } from './GalleryMomentDialog';
import { GalleryUploadDialog } from './GalleryUploadDialog';
import { readGallery, type Gallery, type GalleryAnswer, type GalleryMoment } from './galleryApi';

const t = ru.gallery;
const reactionRefreshMs = 2_000;
const grid = 'grid min-w-0 grid-cols-2 gap-3 desk:grid-cols-3 desk:gap-4';
const refreshEvents = new Set([
  'gallery-moment-published',
  'gallery-moment-hidden',
  'ruleset-changed',
  'command-undone',
  'season-status-changed',
  'season-player-added',
  'player-inactivity-set',
]);

export function GalleryScreen({
  requestedSeasonId,
  currentSeasonId,
  admin,
  onSignedOut,
}: {
  requestedSeasonId: string | null;
  currentSeasonId: string | null;
  admin: boolean;
  onSignedOut: () => void;
}) {
  const scope = requestedSeasonId ?? currentSeasonId;
  const version = useSeasonVersion(scope, {
    relevant: ({ types }) =>
      types.some((type) => type === 'ruleset-changed' || type === 'command-undone'),
  });
  const loadSeasons = useCallback(async () => answerOf(await api.GET('/api/seasons/gallery')), []);
  const seasons = useLoaded(loadSeasons, { onSignedOut, version });
  const heading = usePageHeading(seasons.kind !== 'loading', scope);
  const enabled = seasons.kind === 'ready' ? seasons.value : [];
  const selected = requestedSeasonId
    ? enabled.find((season) => season.id.toLowerCase() === requestedSeasonId)
    : (enabled.find((season) => season.id.toLowerCase() === currentSeasonId) ?? enabled[0]);

  const seasonFilter = (seasonId: string) => (
    <Select
      label={t.season}
      value={seasonId}
      onChange={(event) => {
        navigate(paths.gallery(event.currentTarget.value));
      }}
    >
      {!seasonId ? (
        <option value="" disabled>
          {t.chooseSeason}
        </option>
      ) : null}
      {enabled.map((season) => (
        <option key={season.id} value={season.id}>
          {season.name}
        </option>
      ))}
    </Select>
  );
  if (selected) {
    return (
      <GalleryContents
        key={selected.id}
        seasonId={selected.id}
        seasonFilter={seasonFilter(selected.id)}
        heading={heading}
        admin={admin}
        onSignedOut={onSignedOut}
      />
    );
  }

  return (
    <GalleryPage heading={heading}>
      {seasons.kind === 'loading' ? (
        <GallerySkeleton />
      ) : seasons.kind === 'failed' ? (
        <GalleryReadError retry={seasons.reload} tooOften={seasons.tooOften} />
      ) : (
        <>
          {enabled.length ? <div className="max-w-70">{seasonFilter('')}</div> : null}
          <EmptyState
            level={2}
            icon={<Images size={28} aria-hidden />}
            title={requestedSeasonId ? t.noGallery : t.noSeasons}
          />
        </>
      )}
    </GalleryPage>
  );
}

function GalleryContents({
  seasonId,
  seasonFilter,
  heading,
  admin,
  onSignedOut,
}: {
  seasonId: string;
  seasonFilter: ReactNode;
  heading: RefObject<HTMLHeadingElement | null>;
  admin: boolean;
  onSignedOut: () => void;
}) {
  const [playerId, setPlayerId] = useState('');
  const [players, setPlayers] = useState<Gallery['players']>([]);
  const [gallery, setGallery] = useState<GalleryAnswer>({ kind: 'loading' });
  const [pages, setPages] = useState(1);
  const [attempt, setAttempt] = useState(0);
  const [showingMore, setShowingMore] = useState(false);
  const [upload, setUpload] = useState(false);
  const [openItem, setOpenItem] = useState<string | null>(null);
  const [status, setStatus] = useState('');
  const [readIssue, setReadIssue] = useState<'tooOften' | 'failed' | null>(null);
  const [reactionsUnavailable, setReactionsUnavailable] = useState(false);
  const reactionUpdates = useRef({ version: 0, updatedAt: new Map<string, number>() });
  const uploadFocus = useRef<HTMLButtonElement>(null);
  const momentFocus = useRef<HTMLElement>(null);

  const version = useSeasonVersion(seasonId, {
    refreshOnJoin: true,
    debounceMs: 120,
    relevant: ({ types }) => types.some((type) => refreshEvents.has(type)),
  });

  useEffect(() => {
    const controller = new AbortController();
    const startedAt = reactionUpdates.current.version;
    function keepReadyOnFailure(failure: { kind: 'failed'; tooOften?: boolean }) {
      setGallery((current) => (current.kind === 'ready' ? current : failure));
      setReadIssue(failure.tooOften ? 'tooOften' : 'failed');
      setShowingMore(false);
    }
    void readGallery(seasonId, playerId, pages, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return;
        if (result.kind === 'signedOut') onSignedOut();
        if (result.kind === 'failed') {
          keepReadyOnFailure(result);
          return;
        }
        if (result.kind === 'ready') {
          const { version, updatedAt } = reactionUpdates.current;
          setPlayers(result.value.players);
          setGallery((current) => {
            if (current.kind !== 'ready' || version === startedAt) return result;
            // Preserve newer reactions received while this list was loading.
            const newer = new Map(
              current.value.items
                .filter((item) => (updatedAt.get(item.id) ?? 0) > startedAt)
                .map((item) => [item.id, item.reactions]),
            );
            return {
              ...result,
              value: {
                ...result.value,
                items: result.value.items.map((item) => {
                  const reactions = newer.get(item.id);
                  return item.reactions && reactions ? { ...item, reactions } : item;
                }),
              },
            };
          });
        } else {
          setGallery(result);
        }
        setReadIssue(null);
        setShowingMore(false);
      })
      .catch(() => {
        if (!controller.signal.aborted) keepReadyOnFailure({ kind: 'failed' });
      });
    return () => {
      controller.abort();
    };
  }, [attempt, onSignedOut, pages, playerId, seasonId, version]);
  useRestoredScroll(gallery.kind !== 'loading');

  const data = gallery.kind === 'ready' ? gallery.value : null;
  const tokens = new Map(players.map(({ id, token }) => [id, token]));
  const shownItem = data?.items.find((item) => item.id === openItem);

  function retry() {
    if (!data) {
      setGallery({ kind: 'loading' });
      setPages(1);
    }
    setReadIssue(null);
    setAttempt((value) => value + 1);
  }

  function update(change: (current: Gallery) => Gallery, refresh = true) {
    setGallery((current) =>
      current.kind === 'ready' ? { ...current, value: change(current.value) } : current,
    );
    if (refresh) setAttempt((value) => value + 1);
  }

  function closeMoment() {
    setOpenItem(null);
  }

  // A live hide can take away the focused card together with its modal.
  useEffect(() => {
    if (!openItem || shownItem) return;
    const frame = requestAnimationFrame(() => {
      setOpenItem(null);
      (momentFocus.current?.isConnected ? momentFocus.current : heading.current)?.focus();
    });
    return () => {
      cancelAnimationFrame(frame);
    };
  }, [heading, openItem, shownItem]);

  function selectPlayer(id: string) {
    setGallery({ kind: 'loading' });
    setPages(1);
    setShowingMore(false);
    setReadIssue(null);
    setStatus('');
    setPlayerId(id);
  }

  function published(moment: GalleryMoment) {
    setUpload(false);
    setStatus(t.published);
    update((current) =>
      !playerId || moment.playerId === playerId
        ? { ...current, items: [moment, ...current.items.filter((item) => item.id !== moment.id)] }
        : current,
    );
  }

  function removeMoment(id: string) {
    update((current) => ({ ...current, items: current.items.filter((item) => item.id !== id) }));
  }

  function recordReactionUpdate(id: string) {
    const previous = reactionUpdates.current;
    const version = previous.version + 1;
    reactionUpdates.current = { version, updatedAt: new Map(previous.updatedAt).set(id, version) };
  }

  // A closed grid shows no reaction counts. While the dialog is open, a burst reads only that item.
  const reactionTarget = shownItem?.reactions?.length ? shownItem.id : null;
  const reactionVersion = useSeasonVersion(reactionTarget ? seasonId : null, {
    throttleMs: reactionRefreshMs,
    refreshOnJoin: true,
    relevant: ({ types }) => types.includes('gallery-reaction-set'),
  });
  const refreshReactions = useEffectEvent(async (id: string, signal: AbortSignal) => {
    const startedAt = reactionUpdates.current.version;
    try {
      const result = await readGallery(seasonId, playerId, 1, signal, id);
      if (signal.aborted || reactionUpdates.current.version !== startedAt) return;
      if (result.kind === 'signedOut') onSignedOut();
      if (result.kind === 'disabled' || result.kind === 'notFound') {
        setGallery(result);
        return;
      }
      if (result.kind === 'ready') {
        const moment = result.value.items[0];
        if (moment) {
          recordReactionUpdate(id);
          update(
            (current) => ({
              ...current,
              items: current.items.map((item) => (item.id === id ? moment : item)),
            }),
            false,
          );
        } else {
          removeMoment(id);
        }
        setReactionsUnavailable(false);
        return;
      }
    } catch {
      if (signal.aborted || reactionUpdates.current.version !== startedAt) return;
    }
    setReactionsUnavailable(true);
  });

  useEffect(() => {
    if (!reactionTarget) return;
    const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!controller.signal.aborted) await refreshReactions(reactionTarget, controller.signal);
    });
    return () => {
      controller.abort();
    };
  }, [reactionTarget, reactionVersion]);

  async function hide(id: string) {
    try {
      const { response } = await api.POST('/api/admin/seasons/{seasonId}/gallery/{itemId}/hide', {
        params: { path: { seasonId, itemId: id } },
        body: { commandId: newCommandId() },
      });
      if (response.status === 401) onSignedOut();
      if (response.ok || response.status === 404) {
        setStatus(t.hidden);
        removeMoment(id);
        return true;
      }
    } catch {
      // Keep the confirmation and its retry when the answer does not arrive.
    }
    return false;
  }

  const changeReaction: ChangeReaction = async (id, emoji, selected) => {
    try {
      const { response, error } = await api.PUT(
        '/api/seasons/{seasonId}/gallery/{itemId}/reactions',
        {
          params: { path: { seasonId, itemId: id } },
          body: { commandId: newCommandId(), emoji, selected },
        },
      );
      if (response.status === 401) onSignedOut();
      if (response.status === 429) return 'tooOften';
      if (response.status === 404) {
        removeMoment(id);
        return null;
      }
      if (!response.ok || error) return 'refused';
      recordReactionUpdate(id);
      update(
        (current) => ({
          ...current,
          items: current.items.map((item) =>
            item.id === id && item.reactions
              ? {
                  ...item,
                  reactions: item.reactions.map((reaction) =>
                    reaction.emoji === emoji && reaction.selected !== selected
                      ? { ...reaction, selected, count: reaction.count + (selected ? 1 : -1) }
                      : reaction,
                  ),
                }
              : item,
          ),
        }),
        false,
      );
      return null;
    } catch {
      return 'refused';
    }
  };

  return (
    <GalleryPage
      heading={heading}
      action={
        data?.canPublish ? (
          <Button
            ref={uploadFocus}
            variant="main"
            icon={<Plus size={20} aria-hidden />}
            onClick={() => {
              setStatus('');
              setUpload(true);
            }}
          >
            {t.add}
          </Button>
        ) : null
      }
    >
      <div className="grid min-w-0 grid-cols-2 gap-3 desk:max-w-150">
        {seasonFilter}
        {!data || data.items.length || playerId ? (
          <Select
            label={t.player}
            value={playerId}
            disabled={!players.length}
            onChange={(event) => {
              selectPlayer(event.currentTarget.value);
            }}
          >
            <option value="">{t.allPlayers}</option>
            {players.map((player) => (
              <option key={player.id} value={player.id}>
                {player.name}
              </option>
            ))}
          </Select>
        ) : null}
      </div>
      {status ? <Notice tone="success">{status}</Notice> : null}
      {readIssue && data ? (
        <div className="flex flex-wrap items-center gap-2">
          <Notice tone="warning">{readIssue === 'tooOften' ? t.tooOften : t.retryRead}</Notice>
          <Button onClick={retry}>{ru.ui.retry}</Button>
        </div>
      ) : null}
      {gallery.kind === 'loading' ? (
        <GallerySkeleton />
      ) : gallery.kind === 'failed' ? (
        <GalleryReadError retry={retry} tooOften={gallery.tooOften} />
      ) : data ? (
        <>
          {data.items.length ? (
            <ul className={grid} aria-label={t.title}>
              {data.items.map((item) => (
                <li key={item.id} className="min-w-0">
                  <button
                    type="button"
                    aria-label={t.open(item.caption)}
                    aria-haspopup="dialog"
                    data-testid={`gallery-moment-${item.id}`}
                    className="grid size-full min-w-0 cursor-pointer content-start overflow-hidden rounded-lg border-2 border-muted bg-card text-left text-ink transition duration-(--duration-fast) is-focus:focus-ring is-hover:border-ink"
                    onClick={(event) => {
                      momentFocus.current = event.currentTarget;
                      setReactionsUnavailable(false);
                      setOpenItem(item.id);
                    }}
                  >
                    <span className="relative">
                      <GalleryThumbnail item={item} />
                      <span
                        className="absolute top-2 right-2 grid size-8 place-items-center rounded-full bg-card text-ink"
                        aria-hidden
                      >
                        <Expand size={16} />
                      </span>
                    </span>
                    <span className="grid min-w-0 gap-1 p-2 desk:p-3">
                      <span className="line-clamp-2 text-sm font-bold wrap-anywhere desk:text-base">
                        {item.caption}
                      </span>
                      <span className="flex min-w-0 flex-wrap items-baseline justify-between gap-x-2 text-sm text-ink-soft">
                        <span className="flex min-w-0 items-center gap-2">
                          <Sticker
                            player={{
                              name: item.playerName,
                              token: tokens.get(item.playerId) ?? 0,
                            }}
                            size={22}
                          />
                          <span className="min-w-0 truncate">{item.playerName}</span>
                        </span>
                        <time title={moscowTime(item.createdAt)} dateTime={item.createdAt}>
                          {moscowDay(item.createdAt)}
                        </time>
                      </span>
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          ) : (
            <EmptyState
              level={2}
              icon={playerId ? <SearchX size={28} aria-hidden /> : <Images size={28} aria-hidden />}
              title={playerId ? t.filtered : t.empty}
              action={
                playerId ? (
                  <Button
                    onClick={() => {
                      selectPlayer('');
                    }}
                  >
                    {t.clearFilter}
                  </Button>
                ) : null
              }
            />
          )}
          {data.nextBefore ? (
            <Button
              className="justify-self-start"
              loading={showingMore}
              onClick={() => {
                setShowingMore(true);
                setPages((number) => number + 1);
              }}
            >
              {ru.ui.showMore}
            </Button>
          ) : null}
        </>
      ) : (
        <EmptyState level={2} icon={<Images size={28} aria-hidden />} title={t.noGallery} />
      )}
      {upload && data?.canPublish ? (
        <GalleryUploadDialog
          seasonId={seasonId}
          onPublished={published}
          close={() => {
            setUpload(false);
          }}
          returnFocus={uploadFocus}
        />
      ) : null}
      {shownItem ? (
        <GalleryMomentDialog
          key={shownItem.id}
          item={shownItem}
          close={closeMoment}
          admin={admin}
          hide={hide}
          changeReaction={changeReaction}
          reactionsUnavailable={reactionsUnavailable}
          returnFocus={momentFocus}
        />
      ) : null}
    </GalleryPage>
  );
}

function GalleryPage({
  heading,
  action,
  children,
}: {
  heading: RefObject<HTMLHeadingElement | null>;
  action?: ReactNode;
  children: ReactNode;
}) {
  return (
    <main className="mx-auto grid max-w-300 gap-4 px-4 py-6 desk:gap-5 desk:px-8">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1
          ref={heading}
          tabIndex={-1}
          className="font-display text-2xl font-heavy text-balance desk:text-3xl"
        >
          {t.title}
        </h1>
        {action}
      </div>
      {children}
    </main>
  );
}

function GallerySkeleton() {
  return (
    <div className={grid} role="status" aria-label={ru.ui.loading}>
      {[1, 2, 3, 4, 5, 6].map((key) => (
        <div key={key} className="grid min-w-0 gap-2 rounded-lg bg-card p-2">
          <Skeleton className="aspect-video rounded-md" />
          <Skeleton className="h-5" />
          <Skeleton className="h-4 w-1/2" />
        </div>
      ))}
    </div>
  );
}

function GalleryReadError({
  retry,
  tooOften,
}: {
  retry: () => void;
  tooOften?: boolean | undefined;
}) {
  return (
    <ErrorState
      level={2}
      title={t.readFailed}
      text={tooOften ? t.tooOften : t.retryRead}
      onRetry={retry}
    />
  );
}
