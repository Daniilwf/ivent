import { SearchX, Vote } from 'lucide-react';
import { useEffect, useEffectEvent, useId, useLayoutEffect, useRef, useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { usePageHeading, useRestoredScroll } from '../app/router';
import { answerOf, type Answer } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Skeleton } from '../ui/Progress';
import { EmptyState, Notice } from '../ui/States';
import { PollCard } from './PollCard';
import { isOpenPoll } from './pollView';

type Poll = Schemas['PollView'];
type ReadAnswer = Answer<Poll[]> | { kind: 'disabled' };
type RefreshIssue = { accepted: boolean; tooOften: boolean };
type ScreenState =
  | Exclude<ReadAnswer, { kind: 'ready' }>
  | { kind: 'ready'; value: Poll[]; issue: RefreshIssue | null };

type AcceptedVote = { pollId: string; optionId: string };
type Session = {
  abort: AbortController;
  reading: boolean;
  refreshVersion: number;
  voting: boolean;
  acceptedVote: AcceptedVote | null;
};

const refreshEvents = new Set([
  'vote-cast',
  'vote-changed',
  'poll-started',
  'poll-closed',
  'poll-cancelled',
  'ruleset-changed',
  'command-undone',
  'season-player-added',
  'player-finished',
  'player-finish-revoked',
  'season-status-changed',
]);

function isActive(session: Session) {
  return !session.abort.signal.aborted;
}

async function readPolls(seasonId: string, signal: AbortSignal): Promise<ReadAnswer> {
  try {
    const result = await api.GET('/api/seasons/{seasonId}/polls', {
      params: { path: { seasonId } },
      signal,
    });
    if (result.response.status === 409 && rejectionCode(result.error) === 'feature.disabled') {
      return { kind: 'disabled' };
    }
    return answerOf(result);
  } catch {
    return { kind: 'failed' };
  }
}

export type PollsScreenProps = {
  seasonId: string;
  onSignedOut: () => void;
};

export function PollsScreen({ seasonId, onSignedOut }: PollsScreenProps) {
  const scope = seasonId.toLowerCase();
  return <SeasonPolls key={scope} seasonId={scope} onSignedOut={onSignedOut} />;
}

function SeasonPolls({ seasonId, onSignedOut }: PollsScreenProps) {
  const t = ru.polls;
  const title = usePageHeading();
  const main = useRef<HTMLElement>(null);
  const pollFocus = useRef<{ element: Element; pollId: string } | null>(null);
  const [state, setState] = useState<ScreenState>({ kind: 'loading' });
  const [voting, setVoting] = useState(false);
  const [reading, setReading] = useState(false);
  const session = useRef<Session | null>(null);
  const signedOut = useRef(onSignedOut);

  useEffect(() => {
    signedOut.current = onSignedOut;
  }, [onSignedOut]);

  useLayoutEffect(() => {
    const previous = pollFocus.current;
    pollFocus.current = null;
    if (!previous || previous.element.isConnected || document.activeElement !== document.body) {
      return;
    }
    const cards = main.current?.querySelectorAll<HTMLElement>('article[data-poll-id]');
    const card =
      cards && Array.from(cards).find(({ dataset }) => dataset.pollId === previous.pollId);
    const headingId = card?.getAttribute('aria-labelledby');
    if (headingId) document.getElementById(headingId)?.focus();
  }, [state]);

  useEffect(() => {
    const active: Session = {
      abort: new AbortController(),
      reading: false,
      refreshVersion: 0,
      voting: false,
      acceptedVote: null,
    };
    session.current = active;
    return () => {
      active.abort.abort();
    };
  }, []);

  function signOut(active: Session) {
    active.abort.abort();
    setState({ kind: 'signedOut' });
    signedOut.current();
  }

  async function refresh() {
    const active = session.current;
    if (!active || !isActive(active)) return;
    if (active.reading) {
      active.refreshVersion++;
      return;
    }
    active.reading = true;
    setReading(true);
    try {
      let versionAtStart: number;
      do {
        versionAtStart = active.refreshVersion;
        const acceptedVote = active.acceptedVote;
        const answer = await readPolls(seasonId, active.abort.signal);
        if (!isActive(active)) return;
        if (answer.kind === 'signedOut') {
          signOut(active);
          return;
        }
        // This read started before the vote was accepted. Its queued successor can see the vote.
        if (acceptedVote !== active.acceptedVote) continue;
        if (answer.kind === 'ready') {
          active.acceptedVote = null;
          // A poll moving between list groups is remounted. Keep the keyboard on that poll.
          const focused = document.activeElement;
          const card = focused?.closest<HTMLElement>('article[data-poll-id]');
          const pollId = card?.dataset.pollId;
          pollFocus.current =
            focused && card && pollId && main.current?.contains(card)
              ? { element: focused, pollId }
              : null;
          setState({ ...answer, issue: null });
        } else if (answer.kind === 'failed') {
          const issue = { accepted: acceptedVote !== null, tooOften: answer.tooOften === true };
          setState((previous) => (previous.kind === 'ready' ? { ...previous, issue } : answer));
        } else {
          active.acceptedVote = null;
          setState(answer);
        }
      } while (versionAtStart !== active.refreshVersion);
    } finally {
      active.reading = false;
      if (isActive(active)) setReading(false);
    }
  }

  const version = useSeasonVersion(seasonId, {
    refreshOnJoin: true,
    relevant: (update) =>
      update.seasonId.toLowerCase() === seasonId &&
      update.types.some((type) => refreshEvents.has(type)),
  });
  const refreshForVersion = useEffectEvent(() => void refresh());
  useEffect(() => {
    refreshForVersion();
  }, [version]);
  useRestoredScroll(state.kind !== 'loading');

  async function vote(pollId: string, optionId: string) {
    const active = session.current;
    if (!active || !isActive(active) || active.voting) throw new Error();
    active.voting = true;
    setVoting(true);
    try {
      const { response } = await api.POST('/api/seasons/{seasonId}/polls/{pollId}/vote', {
        params: { path: { seasonId, pollId } },
        body: { commandId: newCommandId(), optionId },
        signal: active.abort.signal,
      });
      if (!isActive(active)) return;
      if (response.status === 401) signOut(active);
      if (!response.ok) throw new Error();

      const acceptedVote = { pollId, optionId };
      active.acceptedVote = acceptedVote;
      setState((previous) =>
        previous.kind === 'ready'
          ? {
              ...previous,
              issue: null,
              value: previous.value.map((poll) =>
                poll.id === acceptedVote.pollId ? { ...poll, myVote: acceptedVote.optionId } : poll,
              ),
            }
          : previous,
      );
      void refresh();
    } finally {
      active.voting = false;
      if (isActive(active)) setVoting(false);
    }
  }

  function retry() {
    title.current?.focus({ preventScroll: true });
    void refresh();
  }

  const issue =
    state.kind === 'ready'
      ? state.issue
      : state.kind === 'failed'
        ? { accepted: false, tooOften: state.tooOften === true }
        : null;

  return (
    <main
      ref={main}
      className="mx-auto grid max-w-180 content-start gap-4 px-4 pt-4 pb-10 desk:px-8 desk:pt-8"
    >
      <h1 ref={title} tabIndex={-1} className="font-display text-xl font-heavy outline-none">
        {t.title}
      </h1>
      {state.kind === 'loading' ? (
        <div
          aria-busy="true"
          data-testid="polls-loading"
          className="grid gap-3 rounded-lg bg-card p-4"
        >
          <p className="sr-only">{ru.ui.loading}</p>
          <Skeleton className="h-6 w-3/4" />
          <Skeleton className="h-12 w-full" />
          <Skeleton className="h-12 w-full" />
        </div>
      ) : null}
      {state.kind === 'disabled' || state.kind === 'notFound' ? (
        <EmptyState
          level={2}
          title={state.kind === 'disabled' ? t.disabled : t.notFound}
          icon={
            state.kind === 'disabled' ? (
              <Vote size={28} aria-hidden />
            ) : (
              <SearchX size={28} aria-hidden />
            )
          }
        />
      ) : null}
      {issue ? (
        <div data-private={issue.accepted || undefined} className="grid justify-items-start gap-2">
          <Notice tone={issue.accepted ? 'warning' : 'danger'}>
            {issue.accepted ? t.acceptedRefreshFailed : t.loadFailed}
          </Notice>
          {issue.tooOften ? <p className="text-sm text-ink-soft">{ru.shell.tooOftenText}</p> : null}
          <Button loading={reading} onClick={retry}>
            {issue.accepted ? t.refreshList : ru.ui.retry}
          </Button>
        </div>
      ) : null}
      {state.kind === 'ready' && state.value.length === 0 ? (
        <EmptyState level={2} title={t.empty} icon={<Vote size={28} aria-hidden />} />
      ) : null}
      {state.kind === 'ready' ? (
        <>
          <PollGroup
            title={t.openGroup}
            polls={state.value.filter(isOpenPoll)}
            pending={voting}
            onVote={vote}
          />
          <PollGroup
            title={t.endedGroup}
            polls={state.value.filter((poll) => !isOpenPoll(poll))}
            pending={voting}
            onVote={vote}
          />
        </>
      ) : null}
    </main>
  );
}

function PollGroup({
  title,
  polls,
  pending,
  onVote,
}: {
  title: string;
  polls: Poll[];
  pending: boolean;
  onVote: (pollId: string, optionId: string) => Promise<void>;
}) {
  const titleId = useId();
  if (polls.length === 0) return null;
  return (
    <section aria-labelledby={titleId} className="grid min-w-0 gap-3">
      <h2 id={titleId} className="font-display text-lg font-heavy">
        {title}
      </h2>
      {polls.map((poll) => (
        <PollCard
          key={poll.id}
          poll={poll}
          level={3}
          pending={pending && isOpenPoll(poll)}
          onVote={(optionId) => onVote(poll.id, optionId)}
        />
      ))}
    </section>
  );
}
