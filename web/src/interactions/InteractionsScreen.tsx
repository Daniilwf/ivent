import { SearchX } from 'lucide-react';
import { useEffect, useEffectEvent, useLayoutEffect, useRef, useState } from 'react';
import { rejectionCode, type Schemas } from '../api/client';
import { usePageHeading, useRestoredScroll } from '../app/router';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { PageHeader } from '../ui/PageHeader';
import { Skeleton } from '../ui/Progress';
import { EmptyState, Notice } from '../ui/States';
import { InteractionsContent } from './InteractionsContent';
import {
  readInteractions,
  sendInteractionAction,
  type InteractionAction,
  type InteractionSnapshot,
} from './interactionData';

export type InteractionsScreenProps = {
  seasonId: string | null;
  role: Schemas['CurrentUser']['role'];
  onSignedOut: () => void;
};

const t = ru.interactions;

type ReadState =
  | { kind: 'loading' }
  | { kind: 'notFound' }
  | { kind: 'failed' }
  | { kind: 'ready'; data: InteractionSnapshot; refreshFailed: boolean };

const refreshEvents = new Set([
  'interaction-created',
  'interaction-answered',
  'interaction-withdrawn',
  'interaction-expired',
  'coop-joined',
  'coop-left',
  'coop-reward-split',
  'partner-request-posted',
  'partner-request-closed',
  'duel-started',
  'duel-settled',
  'duel-win-revoked',
  'ruleset-changed',
  'command-undone',
  'season-status-changed',
  'season-deadline-set',
  'season-player-added',
  'player-adjusted',
  'player-inactivity-set',
  'player-finished',
  'player-finish-revoked',
  'game-rolled',
  'game-choice-rolled',
  'run-started',
  'run-completed',
  'run-dropped',
  'run-rejected',
  'run-tech-rerolled',
]);

export function InteractionsScreen({ seasonId, role, onSignedOut }: InteractionsScreenProps) {
  const scope = seasonId?.toLowerCase() ?? null;
  return (
    <SeasonInteractions
      key={`${scope}:${role}`}
      seasonId={scope}
      role={role}
      onSignedOut={onSignedOut}
    />
  );
}

function SeasonInteractions({ seasonId, role, onSignedOut }: InteractionsScreenProps) {
  const heading = usePageHeading();
  const [state, setState] = useState<ReadState>({ kind: 'loading' });
  const [readVersion, setReadVersion] = useState(0);
  const [pending, setPending] = useState<InteractionAction | null>(null);
  const [message, setMessage] = useState<{ tone: 'danger' | 'success'; text: string } | null>(null);
  const write = useRef<AbortController | null>(null);
  const waitingForSnapshot = useRef(false);
  const readGeneration = useRef(0);
  const afterAction = useRef<HTMLElement | null>(null);
  const signOut = useEffectEvent(onSignedOut);
  const version = useSeasonVersion(seasonId, {
    refreshOnJoin: true,
    // One snapshot can use several reads. Coalesce changes from other players within the season's read limit.
    throttleMs: 8_000,
    relevant: (update) =>
      update.seasonId.toLowerCase() === seasonId &&
      update.types.some((type) => refreshEvents.has(type)),
  });

  function refresh() {
    readGeneration.current++;
    setReadVersion((current) => current + 1);
  }

  useEffect(() => {
    if (!seasonId) return;
    const abort = new AbortController();
    const generation = readGeneration.current;
    const outdated = () => abort.signal.aborted || generation !== readGeneration.current;
    function readFailed() {
      if (outdated()) return;
      setState((current) =>
        current.kind === 'ready' ? { ...current, refreshFailed: true } : { kind: 'failed' },
      );
      if (waitingForSnapshot.current) setPending(null);
    }
    void readInteractions(seasonId, role, abort.signal)
      .then((answer) => {
        if (outdated()) return;
        if (answer.kind === 'signedOut') {
          signOut();
        } else if (answer.kind === 'ready' || answer.kind === 'notFound') {
          setState(
            answer.kind === 'ready'
              ? { kind: 'ready', data: answer.value, refreshFailed: false }
              : answer,
          );
          if (waitingForSnapshot.current) setPending(null);
          waitingForSnapshot.current = false;
        } else {
          readFailed();
        }
      })
      .catch(readFailed);
    return () => {
      abort.abort();
    };
  }, [seasonId, role, version, readVersion]);

  useEffect(() => () => write.current?.abort(), []);
  useRestoredScroll(!seasonId || state.kind !== 'loading');

  useLayoutEffect(() => {
    if (state.kind !== 'ready' || state.refreshFailed) return;
    const control = afterAction.current;
    if (!control) return;
    afterAction.current = null;
    if (!control.isConnected && document.activeElement === document.body) heading.current?.focus();
  }, [state, heading]);

  async function act(action: InteractionAction) {
    if (
      !seasonId ||
      write.current ||
      waitingForSnapshot.current ||
      state.kind !== 'ready' ||
      state.refreshFailed
    )
      return;
    const controller = new AbortController();
    write.current = controller;
    setPending(action);
    setMessage(null);
    const control = document.activeElement;
    try {
      const result = await sendInteractionAction(seasonId, action, controller.signal);
      if (controller.signal.aborted) return;
      if (result.response.status === 401) {
        onSignedOut();
      } else if (result.response.ok) {
        waitingForSnapshot.current = true;
        afterAction.current = control instanceof HTMLElement ? control : null;
        setMessage({ tone: 'success', text: t.success[action.kind] });
        refresh();
      } else {
        const code = rejectionCode(result.error);
        setMessage({
          tone: 'danger',
          text: code ? (ru.rejection[code] ?? ru.rejection.unknown) : t.actionFailed,
        });
        if (result.response.status === 409) refresh();
      }
    } catch {
      if (!controller.signal.aborted) setMessage({ tone: 'danger', text: t.actionFailed });
    } finally {
      write.current = null;
      if (!controller.signal.aborted && !waitingForSnapshot.current) setPending(null);
    }
  }

  return (
    <main className="mx-auto grid w-full max-w-180 min-w-0 content-start gap-4 px-4 pt-4 pb-10 desk:px-8 desk:pt-8">
      <PageHeader ref={heading} tabIndex={-1} className="outline-none">
        {t.title}
      </PageHeader>
      {!seasonId || state.kind === 'notFound' ? (
        <EmptyState
          title={seasonId ? t.notFound : t.unavailable}
          icon={<SearchX aria-hidden />}
          level={2}
        />
      ) : state.kind === 'loading' ? (
        <div aria-busy="true" className="grid gap-3 rounded-lg bg-card p-4">
          <span className="sr-only">{ru.ui.loading}</span>
          <Skeleton className="h-6 w-2/3" />
          <Skeleton className="h-12 w-full" />
        </div>
      ) : state.kind === 'failed' ? (
        <EmptyState
          title={t.failed}
          icon={<SearchX aria-hidden />}
          level={2}
          action={<Button onClick={refresh}>{t.refresh}</Button>}
        />
      ) : (
        <>
          {state.refreshFailed ? (
            <div className="grid justify-items-start gap-2">
              <Notice tone="danger">{t.refreshFailed}</Notice>
              <Button onClick={refresh}>{t.refresh}</Button>
            </div>
          ) : null}
          {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
          <InteractionsContent
            snapshot={state.data}
            pending={pending}
            disabled={state.refreshFailed || pending !== null}
            onAction={(action) => void act(action)}
          />
        </>
      )}
    </main>
  );
}
