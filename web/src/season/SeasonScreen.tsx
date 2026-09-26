import { CalendarClock, Flag, Trophy } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { watchSeason } from '../api/realtime';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { CompleteForm, type Completion } from './CompleteForm';
import { ChoiceCard, OfferCard, rollResultTitle } from './RollResult';
import { AvatarSection } from './AvatarSection';
import { ProofSection } from './ProofForm';
import { ManualEffectItem, type EffectOutcome } from './ManualEffectItem';
import { RunActions } from './RunActions';
import { seasonPicture } from './seasonView';
import { linearBoard } from '../board/linearBoard';
import { RunCard } from '../board/GameCards';
import { Leaderboard } from '../board/Leaderboard';
import { MapView } from '../board/MapView';
import type { MomentHandle } from '../board/moment';
import { WheelMoment, type WheelRoll } from '../board/Wheel';
import { Button } from '../ui/Button';
import { BottomSheet } from '../ui/Dialogs';
import { Chip } from '../ui/Marks';
import { Skeleton } from '../ui/Progress';
import { ErrorState, Notice } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { Panel } from '../ui/Surface';

type Season = Schemas['SeasonView'];
type Command =
  | { kind: 'roll' }
  | { kind: 'start' }
  | { kind: 'reroll' }
  | { kind: 'drop' }
  | {
      kind: 'proof';
      runId: string;
      links: string[];
      note: string | null;
      witnessId: string | null;
      files: string[];
    }
  | { kind: 'techReroll'; reason: NonNullable<Schemas['TechRerollReason']>; comment: string | null }
  | { kind: 'complete'; completion: Completion }
  | { kind: 'choose'; choiceId: string; optionId: string }
  | { kind: 'alreadyPlayed'; gameId: string }
  | { kind: 'resolveEffect'; effectId: string; outcome: EffectOutcome; comment: string | null };
type Loaded = { kind: 'season'; season: Season } | { kind: 'signedOut' } | { kind: 'failed' };

/** Sends one game action; a new command id each time, so a retried request acts once (D-68). */
function send(seasonId: string, command: Command) {
  const commandId = crypto.randomUUID();
  const params = { path: { seasonId } };
  switch (command.kind) {
    case 'roll':
      return api.POST('/api/seasons/{seasonId}/roll', { params, body: { commandId } });
    case 'start':
      return api.POST('/api/seasons/{seasonId}/start', { params, body: { commandId } });
    case 'reroll':
      return api.POST('/api/seasons/{seasonId}/reroll', { params, body: { commandId } });
    case 'drop':
      return api.POST('/api/seasons/{seasonId}/drop', { params, body: { commandId } });
    case 'proof':
      return api.POST('/api/seasons/{seasonId}/runs/{runId}/proof', {
        params: { path: { seasonId, runId: command.runId } },
        body: {
          commandId,
          links: command.links,
          note: command.note,
          witnessId: command.witnessId,
          files: command.files,
        },
      });
    case 'techReroll':
      return api.POST('/api/seasons/{seasonId}/tech-reroll', {
        params,
        body: { commandId, reason: command.reason, comment: command.comment },
      });
    case 'complete':
      return api.POST('/api/seasons/{seasonId}/complete', {
        params,
        body: {
          commandId,
          ...command.completion,
          challengeDone: command.completion.challengeDone ?? false,
        },
      });
    case 'choose':
      return api.POST('/api/seasons/{seasonId}/choose', {
        params,
        body: { commandId, choiceId: command.choiceId, optionId: command.optionId },
      });
    case 'alreadyPlayed':
      return api.POST('/api/seasons/{seasonId}/already-played', {
        params,
        body: { commandId, gameId: command.gameId },
      });
    case 'resolveEffect':
      return api.POST('/api/seasons/{seasonId}/effects/{effectId}/resolve', {
        params: { path: { seasonId, effectId: command.effectId } },
        body: { commandId, outcome: command.outcome, comment: command.comment },
      });
  }
}

async function fetchSeason(seasonId: string): Promise<Loaded> {
  try {
    const { data, response } = await api.GET('/api/seasons/{seasonId}', {
      params: { path: { seasonId } },
    });
    if (data) return { kind: 'season', season: data };
    return response.status === 401 ? { kind: 'signedOut' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/** The season's main screen (H2): my turn on top, the map with everyone's tokens, the leaderboard beside it on a desktop
 *  and in a sheet at the bottom of a phone. */
export function SeasonScreen({
  seasonId,
  onSignedOut,
}: {
  seasonId: string;
  onSignedOut: () => void;
}) {
  const [season, setSeason] = useState<Season | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  // Answers may arrive out of order: never replace newer data with an older view of the log.
  const lastSequence = useRef(-1);

  // The last roll whose wheel has stopped (or the one the page opened with); undefined until the first load
  const [landed, setLanded] = useState<number | null | undefined>(undefined);
  const wheel = useRef<MomentHandle>(null);
  const apply = useCallback(
    (loaded: Loaded) => {
      if (loaded.kind === 'signedOut') {
        onSignedOut();
      } else if (loaded.kind === 'failed') {
        setLoadFailed(true);
      } else if (loaded.season.lastSequence >= lastSequence.current) {
        lastSequence.current = loaded.season.lastSequence;
        setSeason(loaded.season);
        // The roll the page opened with is shown at once; only a roll that comes while it is open spins (D-136)
        const opened = loaded.season.me?.roll?.sequence ?? null;
        setLanded((landed) => (landed === undefined ? opened : landed));
        setLoadFailed(false);
      }
    },
    [onSignedOut],
  );

  // Load now, after every committed command of the season (another player's move included), after every
  // reconnection to the hub (names and avatars live outside the season log) and after a catch-up (D-122).
  useEffect(() => {
    let active = true;
    const refresh = () => {
      void fetchSeason(seasonId).then((loaded) => {
        if (active) apply(loaded);
      });
    };
    refresh();
    const stop = watchSeason(seasonId, refresh);
    return () => {
      active = false;
      stop();
    };
  }, [seasonId, apply]);

  async function act(command: Command) {
    setPending(true);
    setMessage(null);
    try {
      const result = await send(seasonId, command);
      if (result.response.status === 401) {
        onSignedOut();
        return;
      }
      if (result.error) {
        const code = rejectionCode(result.error);
        setMessage((code && ru.rejection[code]) ?? ru.rejection.unknown);
      }
      apply(await fetchSeason(seasonId));
    } catch {
      setMessage(ru.app.loadError);
    } finally {
      setPending(false);
    }
  }

  // Turns end at the deadline even before the scheduler closes the season (D-101): no action the server would refuse.
  const pastDeadline = useIsPast(season?.deadline ?? null);
  const cellsKey = season?.cells.map((c) => `${c.id}:${c.type}`).join('|') ?? '';
  // eslint-disable-next-line react-hooks/exhaustive-deps -- the key stands for the cells
  const chain = useMemo(() => linearBoard(season?.cells ?? []), [cellsKey]);
  const view = useMemo(() => seasonPicture(season, chain), [season, chain]);
  const [retrying, setRetrying] = useState(false);

  if (loadFailed && !season)
    return (
      <main className="mx-auto grid max-w-110 px-4 py-10">
        <ErrorState
          level={1}
          title={ru.shell.loadErrorTitle}
          text={ru.shell.loadErrorText}
          onRetry={() => {
            if (retrying) return;
            setRetrying(true);
            void fetchSeason(seasonId)
              .then(apply)
              .finally(() => {
                setRetrying(false);
              });
          }}
        />
      </main>
    );
  if (!season) return <SeasonSkeleton />;

  const me = season.me;
  const waiting =
    (me?.phase === 'rolling' && !me.offer && !me.choice) ||
    (me?.phase === 'playing' && !me.activeRun);
  const turnsOpen = season.status === 'active' && !pastDeadline;
  const choice = turnsOpen && me?.phase === 'rolling' ? me.choice : null;
  const offer = turnsOpen && me?.phase === 'rolling' ? me.offer : null;
  // A roll that came while the page is open spins its wheel first; the server chose everything, the page only shows it
  const fresh =
    (offer ?? choice) && me?.roll && landed !== undefined && me.roll.sequence > (landed ?? 0)
      ? me.roll
      : null;
  const spinSectors = fresh ? fresh.sectors : [];
  const spin: WheelRoll | null = fresh ? wheelRoll(fresh, offer, choice) : null;
  const uncheckedBlocked = me?.unchecked != null && me.unchecked.count >= me.unchecked.limit;
  const { board, players, rows } = view;
  const mine = players.find((p) => p.me);
  const myRow = rows.find((r) => r.player.me);
  const leader = rows[0];
  const routeLength = Math.max(board.cells.length - 1, 1);
  const closing = season.status === 'closing' || (season.status === 'active' && pastDeadline);
  const finished = season.status === 'finished' || season.status === 'archived';

  return (
    <main className="mx-auto grid max-w-300 grid-cols-1 gap-4 px-4 pt-4 pb-28 desk:grid-cols-[auto_minmax(0,1fr)] desk:items-start desk:gap-6 desk:px-8 desk:pb-8">
      <header className="grid gap-2 min-w-0 desk:col-start-1 desk:w-96">
        <h1 className="font-display text-xl font-heavy text-balance">{season.name}</h1>
        <div className="flex flex-wrap gap-2">
          {season.deadline ? (
            <span data-testid="season-deadline">
              <Chip icon={<CalendarClock size={16} aria-hidden />}>
                {ru.season.deadline(moscowTime(season.deadline))}
              </Chip>
            </span>
          ) : null}
          {closing || finished ? (
            <span data-testid="season-status">
              <Chip icon={<Flag size={16} aria-hidden />}>
                {finished ? ru.season.finished : ru.season.closing}
              </Chip>
            </span>
          ) : null}
        </div>
      </header>
      {loadFailed ? (
        <div className="min-w-0 desk:col-start-1 desk:w-96">
          <Notice tone="danger">{ru.app.loadError}</Notice>
        </div>
      ) : null}

      <section
        aria-labelledby="turn-title"
        data-testid="turn"
        className="grid min-w-0 grid-cols-1 gap-4 rounded-lg bg-card p-4 desk:col-start-1 desk:w-96"
      >
        <h2 id="turn-title" className="font-display text-lg font-heavy">
          {!me
            ? ru.turn.spectatorTitle
            : finished
              ? ru.turn.finishedTitle
              : closing
                ? ru.turn.closingTitle
                : ru.turn.title}
        </h2>
        {!me && <p className="text-ink-soft">{ru.turn.spectator}</p>}
        {me && finished && <p className="text-ink-soft">{ru.turn.finishedText}</p>}
        {me && closing && <p className="text-ink-soft">{ru.turn.closingText}</p>}
        {me && (me.manualEffects.length > 0 || me.lastCompleted) ? (
          <section
            aria-labelledby="after-title"
            data-testid="after"
            className="grid min-w-0 grid-cols-1 gap-4 border-b-2 border-muted pb-4"
          >
            <h3 id="after-title" className="font-display font-heavy">
              {me.manualEffects.length > 0 ? ru.turn.todo(me.manualEffects.length) : ru.turn.after}
            </h3>
            {me.manualEffects.length > 0 && (
              <section
                aria-labelledby="effects-title"
                data-testid="manual-effects"
                className="legacy-screens"
              >
                <h4 id="effects-title">{ru.effects.title}</h4>
                <ul>
                  {me.manualEffects.map((effect) => (
                    <ManualEffectItem
                      key={effect.id}
                      effect={effect}
                      pending={pending}
                      resolvable={season.status === 'active' || season.status === 'closing'}
                      onResolve={(outcome, comment) =>
                        void act({ kind: 'resolveEffect', effectId: effect.id, outcome, comment })
                      }
                    />
                  ))}
                </ul>
              </section>
            )}
            {me.lastCompleted && (
              <div className="legacy-screens">
                <p data-testid="last-dice">
                  {me.lastCompleted.status === 'rejected'
                    ? ru.turn.lastRejected(me.lastCompleted.game.title)
                    : ru.turn.lastDice(
                        me.lastCompleted.game.title,
                        [...me.lastCompleted.dice, ...me.lastCompleted.challengeDice].map(
                          (d) => d.value,
                        ),
                        me.lastCompleted.total,
                      )}
                </p>
                {me.lastCompleted.challengeDice.length > 0 && (
                  <p data-testid="last-challenge-dice">
                    {ru.turn.lastChallengeDice(me.lastCompleted.challengeDice.map((d) => d.value))}
                  </p>
                )}
                {me.lastCompleted.review && (
                  <p data-testid="last-review">
                    {ru.turn.lastReview(
                      me.lastCompleted.review.rating,
                      me.lastCompleted.review.text ?? null,
                    )}
                  </p>
                )}
                <ProofSection
                  proof={me.lastCompleted.proof ?? null}
                  pending={pending}
                  witnesses={season.players.filter((p) => p.id !== me.playerId)}
                  onSubmit={(links, note, witnessId, files) => {
                    if (me.lastCompleted) {
                      void act({
                        kind: 'proof',
                        runId: me.lastCompleted.id,
                        links,
                        note,
                        witnessId,
                        files,
                      });
                    }
                  }}
                />
              </div>
            )}
          </section>
        ) : null}
        {waiting && (
          <div className="grid gap-2" aria-busy="true">
            <Skeleton className="h-6 w-2/3" />
            <Skeleton className="h-12 w-full" />
          </div>
        )}
        {me?.phase === 'idle' && turnsOpen && (
          // D-134: at the limit of runs waiting for the admin's check the roll is closed, and the page says why
          <div className="grid gap-2">
            {uncheckedBlocked ? (
              <Notice tone="warning">
                {ru.turn.uncheckedBlocked(me.unchecked?.count ?? 0, me.unchecked?.limit ?? 0)}
              </Notice>
            ) : null}
            <Button
              variant="main"
              data-testid="roll"
              loading={pending}
              disabled={pending || uncheckedBlocked}
              onClick={() => void act({ kind: 'roll' })}
            >
              {ru.turn.roll}
            </Button>
            {!uncheckedBlocked && me.unchecked && me.unchecked.count > 0 ? (
              <p className="text-sm text-ink-soft" data-testid="unchecked">
                {ru.turn.uncheckedWaiting(me.unchecked.count, me.unchecked.limit)}
              </p>
            ) : null}
          </div>
        )}
        {spin && (
          <div data-testid="wheel" className="grid gap-3">
            <WheelMoment
              key={spin.id}
              ref={wheel}
              sectors={spinSectors}
              roll={spin}
              onPhase={(phase) => {
                if (phase !== 'done') return;
                setLanded(spin.id);
                requestAnimationFrame(() => {
                  document.getElementById(rollResultTitle)?.focus();
                });
              }}
            />
            <Button onClick={() => wheel.current?.skip()}>{ru.moments.skip}</Button>
          </div>
        )}
        {/* The run's forms keep plain styles until H4 (the run page) */}
        {choice && !spin && (
          <ChoiceCard
            choice={choice}
            roll={me?.roll ?? null}
            price={me?.nextReroll ?? null}
            pending={pending}
            onChoose={(optionId) => void act({ kind: 'choose', choiceId: choice.id, optionId })}
            onAlreadyPlayed={(gameId) => void act({ kind: 'alreadyPlayed', gameId })}
            onReroll={() => void act({ kind: 'reroll' })}
          />
        )}
        {offer && !spin && (
          <OfferCard
            offer={offer}
            roll={me?.roll ?? null}
            price={me?.nextReroll ?? null}
            pending={pending}
            onStart={() => void act({ kind: 'start' })}
            onAlreadyPlayed={() => void act({ kind: 'alreadyPlayed', gameId: offer.id })}
            onReroll={() => void act({ kind: 'reroll' })}
          />
        )}
        {me?.phase === 'playing' && me.activeRun && (
          <div data-testid="active-run" className="grid gap-4">
            <p className="sr-only">{ru.turn.playing(me.activeRun.game.title)}</p>
            <RunCard
              game={{
                title: me.activeRun.game.title,
                hours: me.activeRun.game.hours ?? null,
                tags: [],
              }}
              left={myRow ? myRow.cellsToFinish : routeLength}
              level={3}
              total={routeLength}
              actions={null}
            />
          </div>
        )}
        {turnsOpen && me?.phase === 'playing' && me.activeRun && (
          <div className="legacy-screens">
            <CompleteForm
              needsHours={me.activeRun.game.hours == null}
              challengesEnabled={me.challengesEnabled}
              pending={pending}
              onComplete={(completion) => void act({ kind: 'complete', completion })}
            />
            <RunActions
              dropHintMinutes={me.dropHintMinutes}
              dropPenalty={me.dropPenalty}
              techRerollOpen={me.techRerollOpen}
              pending={pending}
              onDrop={() => void act({ kind: 'drop' })}
              onTechReroll={(reason, comment) => void act({ kind: 'techReroll', reason, comment })}
            />
          </div>
        )}
        {message && <Notice tone="danger">{message}</Notice>}
      </section>

      <section
        aria-labelledby="map-title"
        className="grid min-w-0 gap-2 desk:sticky desk:top-20 desk:col-start-2 desk:row-span-6 desk:row-start-1"
      >
        <h2 id="map-title" className="sr-only">
          {ru.map.title}
        </h2>
        <MapView
          board={board}
          players={players}
          focus={mine && mine.cell > 0 ? mine.cell : undefined}
          tools="auto"
          className="h-105 rounded-lg border-3 border-ink desk:h-190"
        />
        {/* The map in words: every cell and who stands there (also what the tests and screen readers read) */}
        <ol data-testid="cells" className="sr-only">
          {season.cells.map((cell, i) => {
            const here = season.players.filter((p) => p.cellId === cell.id);
            return (
              <li key={cell.id} data-testid={`cell-${cell.id}`}>
                {cell.type === 'start'
                  ? ru.map.start
                  : cell.type === 'finish'
                    ? ru.map.finish
                    : ru.map.cellNumber(i + 1)}
                {here.map((p) => (
                  <span key={p.id} data-testid={`token-${p.id}`}>
                    {' '}
                    {p.name}
                  </span>
                ))}
              </li>
            );
          })}
        </ol>
      </section>

      <Panel title={ru.leaderboard.title} className="hidden desk:col-start-1 desk:grid desk:w-96">
        <p className="text-sm text-ink-soft">{ru.board.rule}</p>
        <Leaderboard rows={rows} />
      </Panel>

      <div className="legacy-screens min-w-0 desk:col-start-1 desk:w-96">
        <AvatarSection
          onChanged={() => {
            void fetchSeason(seasonId).then(apply);
          }}
        />
      </div>
      {/* On a phone the leaderboard waits in a sheet at the bottom of the screen */}
      <div className="fixed inset-x-0 bottom-0 z-10 border-t-2 border-muted bg-card px-4 pt-2 pb-3 desk:hidden">
        <BottomSheet
          title={ru.leaderboard.title}
          trigger={
            <button
              type="button"
              className="flex min-h-12 w-full cursor-pointer items-center gap-3 rounded-md text-left is-focus:focus-ring"
            >
              {mine ? <Sticker player={mine} size={36} /> : <Trophy size={24} aria-hidden />}
              <span className="grid">
                <strong className="font-display">{ru.leaderboard.title}</strong>
                {myRow && leader ? (
                  <span className="text-sm text-ink-soft">
                    {leader.player.me
                      ? ru.board.sheet(myRow.place, myRow.points)
                      : ru.board.sheetPeek(leader.player.name, myRow.place)}
                  </span>
                ) : leader ? (
                  <span className="text-sm text-ink-soft">
                    {ru.board.sheetLeader(leader.player.name)}
                  </span>
                ) : null}
              </span>
            </button>
          }
        >
          <p className="text-sm text-ink-soft">{ru.board.rule}</p>
          <div ref={showMe}>
            <Leaderboard rows={rows} marked={false} />
          </div>
        </BottomSheet>
      </div>
    </main>
  );
}

/** The server's roll as the wheel plays it: each miss is a stop on the picked category, then the pick */
function wheelRoll(
  roll: Schemas['WheelRollView'],
  offer: Schemas['OfferedGameView'] | null,
  choice: Schemas['ChoiceView'] | null,
): WheelRoll {
  const sector = Math.max(roll.sectors.indexOf(roll.category), 0);
  return {
    id: roll.sequence,
    misses: roll.misses.map((miss) => ({
      sector,
      game: { title: miss.game },
      by: miss.player,
      playing: miss.reason === 'beingPlayed',
    })),
    pick: offer
      ? { sector, game: { title: offer.title } }
      : { sector, game: null, choices: choice?.options.filter((o) => o.game).length ?? 0 },
  };
}

/** The sheet opens on my row: with 16 players it may be below the fold */
function showMe(node: HTMLDivElement | null) {
  const row = node?.querySelector<HTMLElement>('[data-me]');
  // jsdom has no scrolling
  if (row && typeof row.scrollIntoView === 'function') row.scrollIntoView({ block: 'center' });
}

/** The season's first load: the same frame, grey, so nothing jumps when it comes */
function SeasonSkeleton() {
  return (
    <main
      className="mx-auto grid max-w-300 gap-4 px-4 pt-4 desk:grid-cols-[auto_minmax(0,1fr)] desk:px-8"
      aria-busy="true"
    >
      <p className="sr-only">{ru.app.loading}</p>
      <div className="grid content-start gap-4 desk:w-96">
        <Skeleton className="h-8 w-2/3" />
        <Skeleton className="h-48 w-full rounded-lg" />
      </div>
      <Skeleton className="h-105 w-full rounded-lg desk:h-190" />
    </main>
  );
}

// The longest delay setTimeout takes; a farther deadline is checked again when the timer fires.
const maxTimeout = 2_147_483_647;

/** Whether the UTC instant has passed; re-renders when it does. */
function useIsPast(instant: string | null): boolean {
  const at = instant === null ? null : Date.parse(instant);
  const subscribe = useCallback(
    (notify: () => void) => {
      if (at === null) return () => {};
      const timer = setTimeout(notify, Math.min(Math.max(0, at - Date.now()), maxTimeout));
      return () => {
        clearTimeout(timer);
      };
    },
    [at],
  );
  return useSyncExternalStore(subscribe, () => at !== null && Date.now() >= at);
}
