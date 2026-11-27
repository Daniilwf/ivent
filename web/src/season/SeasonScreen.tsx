import { CalendarClock, Coins, Flag, Swords, Trophy } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { Link } from '../app/Link';
import { paths, usePageHeading } from '../app/router';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { useIsPast } from '../app/useIsPast';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { CompleteForm, type Completion } from './CompleteForm';
import { ChoiceCard, OfferCard, RollGenre, rollGameButton, rollResultTitle } from './RollResult';
import { CompletionMoment } from './CompletionMoment';
import { ProofSection } from './ProofForm';
import type { ProofShot } from './proofAttachments';
import { DrawnEventHistory } from './DrawnEventHistory';
import { EventChoice } from './EventChoice';
import { WaitingEvents } from './WaitingEvents';
import { RunConditions } from './RunConditions';
import { ManualEffectItem, type EffectOutcome } from './ManualEffectItem';
import { IrlProofSection } from './IrlProofSection';
import { PlayerSuggestions } from '../suggestions/PlayerSuggestions';
import { EconomySection } from './Inventory';
import { RunActions } from './RunActions';
import { MyFinish } from './MyFinish';
import { NominationResults } from './NominationResults';
import { seasonPicture } from './seasonView';
import { CompletionRulesLink } from '../rules/CompletionRulesLink';
import { linearBoard } from '../board/linearBoard';
import { Cover, RunCard } from '../board/GameCards';
import { branchOptions, legsPath } from '../board/graphBoard';
import { seasonBoard } from '../board/seasonBoard';
import { MapLegend } from '../board/MapLegend';
import { cellById, cellsToFinish } from '../board/geometry';
import { Confetti } from '../board/Confetti';
import { TokenMove } from '../board/TokenMove';
import { BranchChoice, branchTitle } from './BranchChoice';
import { Leaderboard } from '../board/Leaderboard';
import { FeedPreview } from '../feed/FeedPreview';
import { GlobalEventBanner } from '../globalEvents/GlobalEventBanner';
import { MapView, type MapApi } from '../board/MapView';
import type { MomentHandle } from '../board/moment';
import type { Point } from '../board/types';
import { WheelMoment, type WheelRoll } from '../board/Wheel';
import { Button } from '../ui/Button';
import { standaloneLink } from '../ui/buttonStyles';
import { BottomSheet, FormDialog } from '../ui/Dialogs';
import { Chip } from '../ui/Marks';
import { PageHeader } from '../ui/PageHeader';
import { Skeleton } from '../ui/Progress';
import { ErrorState, Notice } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { useDesk } from '../ui/useDesk';
import { cx } from '../ui/cx';
import { Panel } from '../ui/Surface';
import { Summary } from '../ui/Disclosure';

type Season = Schemas['SeasonView'];
type Command =
  | { kind: 'roll' }
  | { kind: 'revealRoll'; revealId: string }
  // D-600: what the page showed goes with the action; the server refuses it when the turn has changed since
  | { kind: 'start'; gameId: string; difficulty: Schemas['Difficulty'] }
  | { kind: 'reroll'; expected: Schemas['RerollPayment'] | null }
  | { kind: 'drop'; runId: string }
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
  | { kind: 'choose'; choiceId: string; optionId: string; difficulty?: Schemas['Difficulty'] }
  | { kind: 'alreadyPlayed'; gameId: string }
  | { kind: 'resolveEffect'; effectId: string; outcome: EffectOutcome; comment: string | null }
  | { kind: 'irlProof'; effectId: string; files: string[]; link: string | null };
type Loaded = { kind: 'season'; season: Season } | { kind: 'signedOut' } | { kind: 'failed' };
function runDraftKey(season: Season | null, runId: string) {
  return `${season?.id ?? ''}:${season?.me?.playerId ?? ''}:${runId}`;
}

/** The legs of one move of my token, as the server walked them (the graph map) */
type Legs = Schemas['MoveLegView'][];
/** A completion that came while the page is open: its run, the cell my token left and, on the graph, its legs */
type Thrown = { run: string; from: string; before: Season; legs: Legs | null };
/** My token's move after a branch choice, while the page is open (the graph map, D-304) */
type Walking = { sequence: number; legs: Legs; before: Season };

/** The last completed run's dice line takes the focus when its moment ends */
const lastDiceId = 'last-dice';
const phoneMomentClass =
  'fixed inset-x-4 bottom-20 z-20 grid max-h-[calc(100dvh-var(--spacing)*24)] gap-3 overflow-y-auto rounded-lg border-3 border-ink bg-card p-3 shadow-lift';
const desktopDrawClass =
  'season-draw-panel fixed bottom-20 left-1/2 z-20 grid -translate-x-1/2 gap-3 overflow-y-auto rounded-lg border-3 border-ink bg-card p-3 shadow-lift';

function focusPhoneMoment(panel: HTMLDivElement | null) {
  if (panel && document.activeElement === document.body)
    panel.querySelector('button')?.focus({ preventScroll: true });
}

function inViewport(point: Point | null): point is Point {
  return (
    point !== null &&
    point.x >= 0 &&
    point.x < window.innerWidth &&
    point.y >= 0 &&
    point.y < window.innerHeight
  );
}

/** The latest completed run follows the checks, which come newest first. */
function oldestUnsentProof(turn: Schemas['MyTurnView']) {
  return [...(turn.checks ?? [])]
    .reverse()
    .concat(turn.lastCompleted ? [turn.lastCompleted] : [])
    .find((run) => run.status === 'completed' && !run.proof);
}

/** Sends one game action; a new command id each time, so a retried request acts once (D-68). */
function send(seasonId: string, command: Command) {
  const commandId = newCommandId();
  const params = { path: { seasonId } };
  switch (command.kind) {
    case 'roll':
      return api.POST('/api/seasons/{seasonId}/roll', { params, body: { commandId } });
    case 'revealRoll':
      return api.POST('/api/seasons/{seasonId}/roll/reveal', {
        params,
        body: { commandId, revealId: command.revealId },
      });
    case 'start':
      return api.POST('/api/seasons/{seasonId}/start', {
        params,
        body: { commandId, gameId: command.gameId, difficulty: command.difficulty },
      });
    case 'reroll':
      return api.POST('/api/seasons/{seasonId}/reroll', {
        params,
        body: { commandId, expectedPayment: command.expected },
      });
    case 'drop':
      return api.POST('/api/seasons/{seasonId}/drop', {
        params,
        body: { commandId, runId: command.runId },
      });
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
        // A game choice starts the run on its difficulty (D-633); a branch has none
        body: {
          commandId,
          choiceId: command.choiceId,
          optionId: command.optionId,
          ...(command.difficulty ? { difficulty: command.difficulty } : {}),
        },
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
    case 'irlProof':
      return api.POST('/api/seasons/{seasonId}/effects/{effectId}/irl-proof', {
        params: { path: { seasonId, effectId: command.effectId } },
        body: { commandId, files: command.files, link: command.link },
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
  role = 'spectator',
  onSignedOut,
  onNavigationLoaded,
}: {
  seasonId: string;
  /** Who looks at the season: without a token of my own the turn card says why (K2-1) */
  role?: 'player' | 'admin' | 'spectator';
  onSignedOut: () => void;
  onNavigationLoaded?: (navigation: Schemas['SeasonNavigationView']) => void;
}) {
  const [season, setSeason] = useState<Season | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  // Back to the season from another page of the site: the reader starts at its name
  const heading = usePageHeading(season !== null);
  const [pending, setPending] = useState(false);
  const [completionDraftRun, setCompletionDraftRun] = useState<string | null>(null);
  const [preparedShots, setPreparedShots] = useState<ReadonlyMap<string, ProofShot>>(
    () => new Map(),
  );
  const completionOpener = useRef<HTMLButtonElement>(null);
  const [legendOpen, setLegendOpen] = useState(false);
  const legendOpener = useRef<HTMLButtonElement>(null);
  const [leadersOpen, setLeadersOpen] = useState(false);
  const leadersOpener = useRef<HTMLButtonElement>(null);
  const content = useRef<HTMLElement>(null);
  const [scrollFloor, setScrollFloor] = useState(0);
  const [message, setMessage] = useState<string | null>(null);
  const [historyFold, setHistoryFold] = useState({ through: 0, version: 0 });
  // Answers may arrive out of order: never replace newer data with an older view of the log.
  const lastSequence = useRef(-1);

  // The last roll whose wheel has stopped (or the one the page opened with); undefined until the first load
  const [landed, setLanded] = useState<number | null | undefined>(undefined);
  const wheel = useRef<MomentHandle>(null);
  const [revealStep, setRevealStep] = useState<{
    id: string;
    phase: 'opening' | 'spinning' | 'shown';
  } | null>(null);
  const revealRequest = useRef<string | null>(null);
  const alreadyPlayedRoll = useRef<{ sequence: number; category: string } | null>(null);
  // Desktop: the roll whose landed wheel still stands on the map's stage; and the result said to screen readers
  const [staged, setStaged] = useState<number | null>(null);
  const [animatedRoll, setAnimatedRoll] = useState<number | null>(null);
  const [announced, setAnnounced] = useState('');
  // The last completed run and my cell of the last view shown; undefined until the first load. A new completed run
  // brings its dice and my token's move (H4); the one the page opened with is shown at once.
  const seen = useRef<
    | {
        run: string | null;
        cell: string | null;
        points: number;
        shown: Set<string>;
        move: number | null;
      }
    | undefined
  >(undefined);
  const [thrown, setThrown] = useState<Thrown | null>(null);
  const [walking, setWalking] = useState<Walking | null>(null);
  const [movingRun, setMovingRun] = useState<string | null>(null);
  const [rollBefore, setRollBefore] = useState<Season | null>(null);
  const [drawBefore, setDrawBefore] = useState<Season | null>(null);
  const [celebration, setCelebration] = useState<'waiting' | 'playing' | null>(null);
  const [finishRunId, setFinishRunId] = useState<string | null>(null);
  const arrivedAt = useRef<Point | null>(null);
  const walk = useRef<MomentHandle>(null);
  const skippedWalk = useRef<number | null>(null);
  // The season as the page showed it last: while the dice roll, the map and the leaderboard keep it (the moment owns
  // its result, H4 design review)
  const shownSeason = useRef<Season | null>(null);
  // The dice's result, shown big in the turn card once the token stands
  const [thrownResult, setThrownResult] = useState<{
    run: string;
    text: string;
    finishing: boolean;
  } | null>(null);
  const desk = useDesk();
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
        const view = loaded.season;
        const activeRunId = view.me?.activeRun?.id;
        setCompletionDraftRun((draft) =>
          activeRunId && draft === runDraftKey(view, activeRunId) ? draft : null,
        );
        const roll = view.me?.roll;
        const opened = roll?.sequence ?? null;
        const continued = alreadyPlayedRoll.current;
        // An already-played free reroll into the same genre keeps its first step settled.
        const sameGenre =
          roll?.revealId &&
          continued &&
          roll.sequence > continued.sequence &&
          roll.category === continued.category;
        setLanded((landed) => (landed === undefined || sameGenre ? opened : landed));
        if (sameGenre) {
          requestAnimationFrame(() => {
            document.getElementById(rollGameButton)?.focus({ preventScroll: true });
          });
        }
        const previous = shownSeason.current;
        if (previous && opened !== null && previous.me?.roll?.sequence !== opened) {
          setRollBefore(previous);
          setAnnounced('');
        }
        // Decide on the first snapshot for this roll. The public timer must not finish an open player's step.
        if (roll?.revealId && previous?.me?.roll?.revealId !== roll.revealId && view.me) {
          const key = rollStorageKey(view.id, view.me.playerId, roll.sequence);
          const shown = readRollMarker(key, roll.revealId);
          setRevealStep(
            shown || (roll.revealed && !freshRoll(roll))
              ? { id: roll.revealId, phase: 'shown' }
              : null,
          );
        }
        const draw = view.me?.lastEvent?.sequence;
        if (previous && draw != null && previous.me?.lastEvent?.sequence !== draw)
          setDrawBefore(previous);
        const last = view.me?.lastCompleted ?? null;
        if (!view.me?.finish) {
          setCelebration(null);
          setFinishRunId(null);
        } else if (!shownSeason.current?.me?.finish) {
          // An open page sees the finishing run. On reload the turn has no finish run id, so use the oldest missing proof.
          const run = shownSeason.current?.me ? last : (oldestUnsentProof(view.me) ?? last);
          setFinishRunId(run?.id ?? null);
        }
        const mine = view.players.find((p) => p.id === view.me?.playerId);
        const cell = mine?.cellId ?? null;
        const points = mine?.points ?? 0;
        const before = seen.current;
        const shown = before?.shown ?? new Set<string>();
        const at = (id: string | null) => view.cells.findIndex((c) => c.id === id);
        const graph = view.mapMode === 'graph';
        // My latest move, when it is new to this page (the graph map gives it with its legs)
        const move = view.me?.lastMove ?? null;
        const newMove =
          graph && before && move && move.sequence > (before.move ?? 0) ? move.legs : null;
        // Only a completion new to this page plays: not the one it opened with, not an older run an admin's rollback
        // brings back (the token goes back and the points drop), not a run already shown. On the graph a snake may take
        // the token back: its cell's order says nothing
        const completed =
          before?.cell &&
          last &&
          last.id !== before.run &&
          !shown.has(last.id) &&
          last.status !== 'rejected' &&
          (graph || at(cell) >= at(before.cell)) &&
          points >= before.points;
        if (completed)
          setThrown(
            shownSeason.current && before.cell
              ? {
                  run: last.id,
                  from: before.cell,
                  before: shownSeason.current,
                  legs: newMove,
                }
              : null,
          );
        else if (last?.id !== before?.run || last?.status === 'rejected') setThrown(null);
        // The rest of a move after the branch choice: the token walks the chosen branch
        if (
          !completed &&
          newMove &&
          move &&
          newMove[0]?.reason === 'completionRoll' &&
          shownSeason.current
        )
          setWalking({ sequence: move.sequence, legs: newMove, before: shownSeason.current });
        if (last) shown.add(last.id);
        seen.current = { run: last?.id ?? null, cell, points, shown, move: move?.sequence ?? null };
        shownSeason.current = view;
        setLoadFailed(false);
      }
    },
    [onSignedOut],
  );

  const navigationId = season?.id;
  const pollsEnabled = season?.pollsEnabled === true;
  const galleryEnabled = season?.galleryEnabled === true;
  const interactionFeatures = season?.interactionFeatures ?? null;
  useEffect(() => {
    if (navigationId)
      onNavigationLoaded?.({ id: navigationId, pollsEnabled, galleryEnabled, interactionFeatures });
  }, [navigationId, onNavigationLoaded, pollsEnabled, galleryEnabled, interactionFeatures]);

  // Load now, after every committed command of the season (another player's move included), after every
  // reconnection to the hub (names and avatars live outside the season log) and after a catch-up (D-122).
  const version = useSeasonVersion(seasonId, {
    snapshotSequence:
      season?.id.toLowerCase() === seasonId.toLowerCase() ? season.lastSequence : null,
  });
  useEffect(() => {
    let active = true;
    void fetchSeason(seasonId).then((loaded) => {
      if (active) apply(loaded);
    });
    return () => {
      active = false;
    };
  }, [seasonId, apply, version]);

  function setPreparedShot(runId: string, shot?: ProofShot) {
    const key = runDraftKey(season, runId);
    setPreparedShots((previous) => {
      if (!shot && !previous.has(key)) return previous;
      const next = new Map(previous);
      if (shot) next.set(key, shot);
      else next.delete(key);
      return next;
    });
  }

  async function act(command: Command) {
    const drawBeforeCommand = season?.me?.lastEvent?.sequence ?? 0;
    if (command.kind === 'alreadyPlayed' && season?.me?.roll) {
      alreadyPlayedRoll.current = {
        sequence: season.me.roll.sequence,
        category: season.me.roll.category,
      };
    }
    // Starting a new action also skips any celebration still waiting for the previous card.
    setCelebration(null);
    arrivedAt.current = null;
    // Keep the current viewport reachable if this action replaces a tall form with a fixed moment.
    if (!desk && content.current)
      setScrollFloor(
        Math.max(0, Math.ceil(window.innerHeight - content.current.getBoundingClientRect().top)),
      );
    setPending(true);
    setMessage(null);
    try {
      const result = await send(seasonId, command);
      if (result.response.status === 401) {
        onSignedOut();
        return false;
      }
      if (result.error) {
        const code = rejectionCode(result.error);
        setMessage((code && ru.rejection[code]) ?? ru.rejection.unknown);
      }
      if (result.response.ok && !result.error) {
        setHistoryFold((previous) => ({
          through: Math.max(previous.through, drawBeforeCommand),
          version: previous.version + 1,
        }));
        if (command.kind === 'proof') setPreparedShot(command.runId);
      }
      apply(await fetchSeason(seasonId));
      return result.response.ok && !result.error;
    } catch {
      setMessage(ru.app.loadError);
      return false;
    } finally {
      if (command.kind === 'alreadyPlayed') alreadyPlayedRoll.current = null;
      setPending(false);
    }
  }

  // Turns end at the deadline even before the scheduler closes the season (D-101): no action the server would refuse.
  const pastDeadline = useIsPast(season?.deadline ?? null);
  const graphMode = season?.mapMode === 'graph';
  // The map is drawn again only when it changes (a publication), not on every refresh of the season
  const mapKey = season
    ? JSON.stringify([
        season.mapMode,
        season.cells,
        graphMode ? season.edges : [],
        season.zones,
        season.background ?? null,
      ])
    : '';
  const chain = useMemo(
    () => (season ? seasonBoard(season) : linearBoard([])),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the key stands for the map
    [mapKey],
  );
  const view = useMemo(() => seasonPicture(season, chain), [season, chain]);
  const held = useMemo(
    () => (thrown ? seasonPicture(thrown.before, chain) : null),
    [thrown, chain],
  );
  const walkingHeld = useMemo(
    () => (walking ? seasonPicture(walking.before, chain) : null),
    [walking, chain],
  );
  const rollHeld = useMemo(() => seasonPicture(rollBefore, chain), [rollBefore, chain]);
  const drawHeld = useMemo(() => seasonPicture(drawBefore, chain), [drawBefore, chain]);
  // Worked out once per move: a new path in the middle of the walk would start the token over
  const walkingPath = useMemo(
    () => (walking ? legsPath(walking.legs, chain.cellNumber) : null),
    [walking, chain],
  );
  // A branch choice that comes while the page is open takes the focus, as a rolled game does
  const branchId = season?.me?.choice?.kind === 'branch' ? season.me.choice.id : null;
  const branchSeen = useRef<string | null | undefined>(undefined);
  // On a desktop the choice stands at the bottom of the map: the camera shows the fork and its branches above it
  const camera = useRef<MapApi>(null);
  const mapFrame = useRef<HTMLDivElement>(null);
  const forkCell =
    branchId && season
      ? chain.cellNumber.get(season.players.find((p) => p.id === season.me?.playerId)?.cellId ?? '')
      : undefined;
  useEffect(() => {
    if (thrown || walking) return;
    // The choice made, the camera goes back to the map's own view, around my new cell
    if (forkCell === undefined) {
      camera.current?.reset();
      return;
    }
    if (!desk) return;
    const fork = cellById(chain.board, forkCell);
    requestAnimationFrame(() => {
      camera.current?.centerOn({ x: fork.x, y: fork.y + 140 }, 900);
    });
  }, [desk, forkCell, chain, branchId, thrown, walking]);
  useEffect(() => {
    if (thrown || walking) return;
    if (branchSeen.current !== undefined && branchId && branchId !== branchSeen.current)
      requestAnimationFrame(() => {
        document.getElementById(branchTitle)?.focus({ preventScroll: true });
      });
    if (season) branchSeen.current = branchId;
  }, [branchId, season, thrown, walking]);
  // An event drawn while the page is open is drawn on screen (a key moment); one drawn before it opened just stands
  // there. The first loaded view sets the base during render, so the new card mounts already knowing it is fresh.
  const drawnSequence = season?.me?.lastEvent?.sequence ?? null;
  const eventQuestionId = season?.me?.choice?.kind === 'effect' ? season.me.choice.id : null;
  const eventQuestionSeen = useRef<string | null | undefined>(undefined);
  const [drawBase, setDrawBase] = useState<{ sequence: number | null } | null>(null);
  const [shownDraws, setShownDraws] = useState<ReadonlySet<number>>(() => new Set());
  const [settledDraw, setSettledDraw] = useState<number | null>(null);
  const [readDraw, setReadDraw] = useState<number | null>(null);
  const newDraw =
    drawBase !== null && drawnSequence !== null && drawnSequence > (drawBase.sequence ?? 0);
  const freshDraw = newDraw && !shownDraws.has(drawnSequence);
  const drawPending = newDraw && settledDraw !== drawnSequence;
  useEffect(() => {
    if (thrown || walking || drawPending) return;
    if (
      eventQuestionSeen.current !== undefined &&
      eventQuestionId &&
      eventQuestionId !== eventQuestionSeen.current
    )
      requestAnimationFrame(() =>
        document
          .querySelector<HTMLElement>('[data-testid="event-choice"] h3')
          ?.focus({ preventScroll: true }),
      );
    if (season) eventQuestionSeen.current = eventQuestionId;
  }, [eventQuestionId, season, thrown, walking, drawPending]);
  const noteDrawShown = useCallback((sequence: number) => {
    setShownDraws((shown) => (shown.has(sequence) ? shown : new Set([...shown, sequence])));
  }, []);
  if (season && drawBase === null) setDrawBase({ sequence: season.lastSequence });
  const skipDraw = () => {
    if (!drawPending) return;
    noteDrawShown(drawnSequence);
    setSettledDraw(drawnSequence);
  };
  // A difficulty event can arrive with a finish. Follow the current card until it turns face up,
  // including if the server replaces it or removes it before the celebration.
  if (celebration === 'waiting' && !drawPending) setCelebration('playing');
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
  const interactions = season.interactionFeatures;
  const showInteractions =
    interactions?.duels ||
    interactions?.partnerBoard ||
    (role === 'player' && me && (interactions?.coop || me.hasInteractionActions));
  const manualEffects = me?.manualEffects.filter((effect) => !effect.irl) ?? [];
  const irlEffects = me?.manualEffects.filter((effect) => effect.irl) ?? [];
  const effectChoice = me?.choice?.kind === 'effect' ? me.choice : null;
  const answerAllowed =
    me?.eventsEnabled === true && (season.status === 'active' || season.status === 'closing');
  const waiting =
    !effectChoice &&
    ((me?.phase === 'rolling' && !me.offer && !me.choice) ||
      (me?.phase === 'playing' && !me.activeRun));
  const turnsOpen = season.status === 'active' && !pastDeadline;
  const completableRun =
    turnsOpen && !effectChoice && me?.phase === 'playing' && !me.isCoopPartner
      ? me.activeRun
      : null;
  const completionKey = completableRun ? runDraftKey(season, completableRun.id) : null;
  const completing = completionKey !== null && completionKey === completionDraftRun;
  const choice =
    turnsOpen && me?.phase === 'rolling' && me.choice?.kind === 'game' ? me.choice : null;
  // A branch choice is part of a throw made before the deadline: it is answered after it too (D-305)
  const branch = me?.choice?.kind === 'branch' ? me.choice : null;
  const offer = turnsOpen && !effectChoice && me?.phase === 'rolling' ? me.offer : null;
  const currentRoll = offer || choice ? (me?.roll ?? null) : null;
  const splitRoll = currentRoll?.revealId ? currentRoll : null;
  const localReveal = revealStep?.id === splitRoll?.revealId ? revealStep : null;
  const openingGame = localReveal?.phase === 'opening';
  const heldGenre = splitRoll !== null && (localReveal === null || openingGame);
  // An opened page owns the new genre spin. A loaded result starts at the settled genre or the disclosed game.
  const fresh =
    currentRoll && landed !== undefined && currentRoll.sequence > (landed ?? 0)
      ? currentRoll
      : null;
  const spin = fresh ? (splitRoll ? genreWheelRoll(fresh) : wheelRoll(fresh, offer, choice)) : null;
  const gameWheel = splitRoll && offer ? gameWheelRoll(splitRoll, offer) : null;
  const gameSpin = localReveal?.phase === 'spinning' ? gameWheel : null;
  const showRollResult = !spin && !gameSpin && !heldGenre;
  const rollRevealClass =
    !desk && animatedRoll !== null && animatedRoll === me?.roll?.sequence
      ? 'animate-wheel-result'
      : undefined;
  const uncheckedBlocked = me?.unchecked != null && me.unchecked.count >= me.unchecked.limit;
  // At the check limit the one thing to do is a proof: of the oldest run waiting without one (checks come newest first)
  const proofToSend = uncheckedBlocked ? oldestUnsentProof(me) : undefined;
  const { board, players, rows, cellNumber } = view;
  // A completion that came while the page is open throws its dice first; the rest of the turn waits for it
  const throwing =
    thrown && me?.lastCompleted?.id === thrown.run && me.lastCompleted.status !== 'rejected'
      ? me.lastCompleted
      : null;
  const mine = players.find((p) => p.me);
  // While the dice roll, the map and the leaderboard still show the season before the completion
  // …and while my token walks the chosen branch, the map shows me at the fork
  const walked = !throwing && mine ? walkingPath : null;
  // A walk the page cannot draw (a cell it does not know) is not played
  const moving = walked ? walking : null;
  const drawOnStage =
    newDraw &&
    !throwing &&
    !moving &&
    drawnSequence !== readDraw &&
    drawnSequence > historyFold.through;
  // The same card stays mounted when it leaves the stage for the turn's history or question.
  const drawStageClass = drawOnStage ? (desk ? desktopDrawClass : phoneMomentClass) : undefined;
  const closeDraw =
    drawOnStage && !drawPending ? (
      <Button
        variant="quiet"
        className="justify-self-start"
        onClick={() => {
          setReadDraw(drawnSequence);
        }}
      >
        {ru.moments.toTurn}
      </Button>
    ) : null;
  const shownPlayers =
    throwing && held ? held.players : moving && walkingHeld ? walkingHeld.players : players;
  const beforeMoment =
    (throwing ? thrown?.before : null) ??
    moving?.before ??
    (spin || gameSpin ? rollBefore : null) ??
    (drawPending ? drawBefore : null);
  const momentRows = throwing
    ? held?.rows
    : moving
      ? walkingHeld?.rows
      : (spin || gameSpin) && rollBefore
        ? rollHeld.rows
        : drawPending && drawBefore
          ? drawHeld.rows
          : null;
  const shownRows = momentRows ?? rows;
  const shownMine = shownPlayers.find((p) => p.me);
  const myRow = shownRows.find((r) => r.player.me);
  const topRows = shownRows.slice(0, 5);
  if (myRow && !topRows.includes(myRow)) topRows.push(myRow);
  const leader = shownRows[0];
  const routeLength = graphMode
    ? Math.max(cellsToFinish(board, 1), 1)
    : Math.max(board.cells.length - 1, 1);
  const closing = season.status === 'closing' || (season.status === 'active' && pastDeadline);
  const finished = season.status === 'finished' || season.status === 'archived';
  // K1S-13: a draft has not started: nothing to do yet, and nobody leads
  const draft = season.status === 'draft';
  const checks = me?.checks ?? [];
  // K1S-19: everyone level at the top (the season's start) — nobody is called the leader
  const leaderTied =
    leader !== undefined && !leader.isFirst && shownRows.filter((r) => r.place === 1).length > 1;

  // The current step comes first — a roll waiting for my answer (H3) or the game I play (H4) — and the tails of the
  // last run fold under it
  const stepFirst =
    Boolean(effectChoice ?? offer ?? choice) || (me?.phase === 'playing' && me.activeRun != null);
  // H2 keeps a manual task before the next roll; historical results and non-blocking IRL proofs stay below it.
  const manualFirst = !stepFirst && me?.manualEffects.some((effect) => !effect.irl) === true;
  // The idle turn's main slot: a roll, or at the check limit the proof that opens it again
  const rollSlot = me?.phase === 'idle' && turnsOpen && !throwing && !me.choice && !moving;
  const finishRun =
    me?.lastCompleted?.id === finishRunId
      ? me.lastCompleted
      : checks.find((run) => run.id === finishRunId);
  const finishProof =
    me?.finish &&
    !me.finish.frozen &&
    !throwing &&
    !moving &&
    !drawPending &&
    !me.choice &&
    !manualFirst &&
    (rollSlot || closing) &&
    finishRun?.status === 'completed' &&
    !finishRun.proof
      ? finishRun
      : undefined;
  const mainProof = finishProof ?? (rollSlot ? proofToSend : undefined);
  const lastProofShot =
    me?.lastCompleted?.status === 'completed'
      ? preparedShots.get(runDraftKey(season, me.lastCompleted.id))
      : undefined;
  /** Opens the proof form of a run on the card and puts the focus in it */
  const openProof = (runId: string) => {
    const fold = document.querySelector<HTMLDetailsElement>(`[data-proof-run="${runId}"]`);
    if (!fold) return;
    fold.open = true;
    const field = Array.from(
      fold.querySelectorAll<HTMLElement>('input, textarea, select, button[type="submit"]'),
    ).find((control) => !control.closest('details:not([open])'));
    (field ?? fold.querySelector('summary'))?.focus();
  };
  const revealPreparedProof = (event: MouseEvent<HTMLDetailsElement>) => {
    const fold = event.currentTarget;
    const summary = fold.querySelector('summary');
    if (fold.open || !(event.target instanceof Node) || !summary?.contains(event.target)) return;
    requestAnimationFrame(() => {
      // The ready form fits under its summary once the mobile leaderboard dock is out of its way.
      if (fold.open) summary.scrollIntoView({ block: 'center', behavior: 'instant' });
    });
  };
  const afterRun =
    me &&
    (manualEffects.length > 0 ||
      me.lastCompleted ||
      (me.lastEvent && effectChoice?.objectId !== me.lastEvent.objectId) ||
      checks.length > 0) ? (
      <section
        aria-labelledby="after-title"
        data-testid="after"
        className={cx(
          'grid min-w-0 grid-cols-1 gap-4 border-muted',
          stepFirst ? 'border-t-2 pt-4' : 'border-b-2 pb-4',
        )}
      >
        <h3 id="after-title" className="font-display font-heavy">
          {manualEffects.length > 0
            ? ru.effects.taskTitle
            : stepFirst && me.lastCompleted
              ? ru.turn.afterGame(me.lastCompleted.game.title)
              : me.lastCompleted || me.lastEvent
                ? ru.turn.after
                : ru.turn.checksTitle}
        </h3>
        {manualEffects.length > 0 && (
          <section aria-labelledby="after-title" data-testid="manual-effects">
            <ul className="grid gap-2">
              {manualEffects.map((effect, index) => (
                <ManualEffectItem
                  key={effect.id}
                  effect={effect}
                  primary={manualFirst && index === 0}
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
          <div className="grid min-w-0 gap-2">
            {thrownResult?.run === me.lastCompleted.id ? (
              <p
                data-testid="throw-result"
                className={cx(!thrownResult.finishing && 'font-display text-xl font-heavy')}
              >
                {thrownResult.text}
              </p>
            ) : null}
            <details
              id={`proof-${me.lastCompleted.id}`}
              data-testid="proof-details"
              data-proof-run={me.lastCompleted.id}
              open={finishProof?.id === me.lastCompleted.id}
              onClick={
                !desk && !me.lastCompleted.proof && lastProofShot ? revealPreparedProof : undefined
              }
              className="grid gap-2"
            >
              <Summary className="text-sm">
                <span id={lastDiceId} data-testid="last-dice" className="font-medium">
                  {me.lastCompleted.status === 'rejected'
                    ? ru.turn.lastRejected(me.lastCompleted.game.title)
                    : ru.turn.lastDice(
                        me.lastCompleted.game.title,
                        [...me.lastCompleted.dice, ...me.lastCompleted.challengeDice].map(
                          (d) => d.value,
                        ),
                        me.lastCompleted.total,
                        [
                          ...new Set(
                            [...me.lastCompleted.dice, ...me.lastCompleted.challengeDice].map(
                              (d) => d.sides,
                            ),
                          ),
                        ],
                      )}
                </span>{' '}
                <span className="text-ink-soft">
                  {me.lastCompleted.proof
                    ? ru.turn.proofSummary
                    : lastProofShot
                      ? ru.turn.proofShotReady
                      : ru.turn.proofToSend}
                </span>
              </Summary>
              {me.lastCompleted.game.cover ? (
                <Cover
                  game={{
                    title: me.lastCompleted.game.title,
                    cover: me.lastCompleted.game.cover.thumbnailUrl,
                  }}
                  width={40}
                  className="mt-1 ml-1"
                />
              ) : null}
              {me.lastCompleted.challengeDice.length > 0 && (
                <p data-testid="last-challenge-dice" className="text-sm text-ink-soft">
                  {ru.turn.lastChallengeDice(me.lastCompleted.challengeDice.map((d) => d.value))}
                </p>
              )}
              {me.lastCompleted.review && (
                <p data-testid="last-review" className="text-sm text-ink-soft wrap-anywhere">
                  {ru.turn.lastReview(
                    me.lastCompleted.review.rating,
                    me.lastCompleted.review.text ?? null,
                  )}
                </p>
              )}
              {me.eventsEnabled === true ? (
                <RunConditions
                  conditions={me.lastCompleted.conditions ?? null}
                  check={me.lastCompleted.conditionCheck ?? null}
                  resultNote={conditionResultNote(me.lastCompleted)}
                />
              ) : null}
              <ProofSection
                key={`${me.lastCompleted.id}:${JSON.stringify(me.lastCompleted.proof ?? null)}`}
                proof={me.lastCompleted.proof ?? null}
                pending={pending}
                preparedShot={lastProofShot}
                onRemovePreparedShot={() => {
                  if (me.lastCompleted) setPreparedShot(me.lastCompleted.id);
                }}
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
            </details>
          </div>
        )}
        {/* K1S-1, K1S-9: the other runs that still take a proof, and a rejection since my last roll */}
        {checks.length > 0 && (
          <section aria-labelledby="checks-title" data-testid="checks" className="grid gap-3">
            {me.lastCompleted ? (
              <h4 id="checks-title" className="font-bold">
                {ru.turn.checksTitle}
              </h4>
            ) : (
              <span id="checks-title" className="sr-only">
                {ru.turn.checksTitle}
              </span>
            )}
            <ul className="grid gap-4">
              {checks.map((run) => (
                <li key={run.id} data-testid={`check-${run.id}`} className="grid min-w-0 gap-2">
                  <p className="min-w-0 font-medium">
                    {run.status === 'rejected'
                      ? ru.turn.checkRejected(run.game.title)
                      : ru.turn.checkWaiting(run.game.title, run.total)}
                  </p>
                  {me.eventsEnabled === true ? (
                    <RunConditions
                      conditions={run.conditions ?? null}
                      check={run.conditionCheck ?? null}
                      resultNote={conditionResultNote(run)}
                    />
                  ) : null}
                  {run.status === 'rejected' ? (
                    <ProofSection
                      proof={run.proof ?? null}
                      pending={pending}
                      witnesses={[]}
                      onSubmit={() => {}}
                    />
                  ) : (
                    <details
                      id={`proof-${run.id}`}
                      data-testid="check-proof"
                      data-proof-run={run.id}
                      open={finishProof?.id === run.id}
                      onClick={
                        !desk && !run.proof && preparedShots.has(runDraftKey(season, run.id))
                          ? revealPreparedProof
                          : undefined
                      }
                      className="grid gap-2"
                    >
                      <Summary className="font-bold">
                        {run.proof
                          ? ru.turn.proofSummaryFor(run.game.title)
                          : ru.turn.proofToSendFor(run.game.title)}
                      </Summary>
                      <ProofSection
                        key={`${run.id}:${JSON.stringify(run.proof ?? null)}`}
                        proof={run.proof ?? null}
                        pending={pending}
                        preparedShot={preparedShots.get(runDraftKey(season, run.id))}
                        onRemovePreparedShot={() => {
                          setPreparedShot(run.id);
                        }}
                        witnesses={season.players.filter((p) => p.id !== me.playerId)}
                        onSubmit={(links, note, witnessId, files) => {
                          void act({ kind: 'proof', runId: run.id, links, note, witnessId, files });
                        }}
                      />
                    </details>
                  )}
                </li>
              ))}
            </ul>
          </section>
        )}
        {/* The event comes after the move that stopped on its cell: the card reads after the throw (design review) */}
        {/* Key moments one after another (DESIGN.md «Движение»): a fresh card waits for the token to stop */}
        {me.lastEvent &&
        effectChoice?.objectId !== me.lastEvent.objectId &&
        !(freshDraw && moving) ? (
          <DrawnEventHistory
            key={`${String(me.lastEvent.sequence)}:${String(historyFold.through >= me.lastEvent.sequence ? historyFold.version : 0)}`}
            drawn={me.lastEvent}
            fresh={freshDraw}
            revealing={drawPending}
            className={drawStageClass}
            actions={closeDraw}
            initiallyOpen={newDraw && me.lastEvent.sequence > historyFold.through}
            onShown={noteDrawShown}
            onSettled={newDraw ? setSettledDraw : undefined}
          />
        ) : null}
      </section>
    ) : null;
  const wheelWords = ru.moments.wheel;
  const announceGame = (roll: Schemas['WheelRollView']) => {
    const count = choice?.options.filter((option) => option.game).length ?? 0;
    setAnnounced(
      wheelWords.announce(
        roll.category,
        offer ? wheelWords.result(offer.title) : wheelWords.choice(count),
      ),
    );
    requestAnimationFrame(() => {
      document.getElementById(rollResultTitle)?.focus({ preventScroll: true });
    });
  };

  const showGame = (roll: Schemas['WheelRollView'], revealId: string, keepWheel: boolean) => {
    if (!me) return;
    writeRollMarker(rollStorageKey(season.id, me.playerId, roll.sequence), revealId);
    setRevealStep({ id: revealId, phase: 'shown' });
    setStaged(keepWheel && desk ? roll.sequence : null);
    announceGame(roll);
  };

  async function revealGame(animate: boolean) {
    const revealId = splitRoll?.revealId;
    if (!splitRoll || !revealId || pending || revealRequest.current !== null) return;
    revealRequest.current = revealId;
    setRevealStep({ id: revealId, phase: 'opening' });
    if (spin) wheel.current?.skip();
    const requested = splitRoll.revealed || (await act({ kind: 'revealRoll', revealId }));
    revealRequest.current = null;
    const latestRoll = shownSeason.current?.me?.roll;
    if (latestRoll?.revealId !== revealId) return;
    if (!requested && !latestRoll.revealed) {
      setRevealStep(null);
      return;
    }
    // Another tab or the timer may have published the same result while this request was queued.
    if (!requested) setMessage(null);
    if (animate && gameWheel) {
      setRevealStep({ id: revealId, phase: 'spinning' });
    } else {
      showGame(splitRoll, revealId, false);
    }
  }

  // A settled genre waits in place. After the game wheel lands, the desktop keeps it over the map until I close it.
  const staging = desk && currentRoll && staged === currentRoll.sequence ? currentRoll : null;
  const genreOnStage = splitRoll && (spin || heldGenre) ? splitRoll : null;
  const gameOnStage = gameSpin || (staging && splitRoll ? gameWheel : null);
  const onStage =
    gameOnStage?.roll ??
    spin ??
    (genreOnStage ? genreWheelRoll(genreOnStage) : null) ??
    (staging && !splitRoll ? wheelRoll(staging, offer, choice) : null);
  const wheelKind = gameOnStage ? 'game' : genreOnStage ? 'genre' : undefined;
  const wheelSettled = !spin && !gameSpin;
  const spinningStatus = splitRoll
    ? spin
      ? wheelWords.spinGenre
      : gameSpin
        ? wheelWords.spinGame
        : null
    : desk && spin
      ? wheelWords.spinning
      : null;
  const stage =
    onStage && currentRoll ? (
      <div
        data-testid="wheel"
        data-wheel-kind={wheelKind}
        data-wheel-settled={wheelSettled}
        className={
          desk
            ? 'grid h-full w-full grid-rows-[minmax(0,1fr)_auto] justify-items-center gap-3'
            : 'grid w-full content-center justify-items-center gap-3'
        }
      >
        <WheelMoment
          key={`${wheelKind ?? 'legacy'}:${String(onStage.id)}`}
          ref={wheel}
          sectors={gameOnStage?.sectors ?? currentRoll.sectors}
          roll={onStage}
          kind={wheelKind}
          settled={wheelSettled}
          announce={false}
          fill={desk}
          onAnimatedResult={() => {
            if (wheelKind !== 'genre') setAnimatedRoll(onStage.id);
          }}
          onPhase={(phase) => {
            if (phase !== 'done') return;
            if (wheelKind === 'game' && splitRoll?.revealId) {
              showGame(splitRoll, splitRoll.revealId, true);
            } else {
              setLanded(onStage.id);
              if (wheelKind === 'genre') {
                setAnnounced(wheelWords.genre(currentRoll.category));
                requestAnimationFrame(() => {
                  document.getElementById(rollGameButton)?.focus({ preventScroll: true });
                });
              } else {
                if (desk) setStaged(onStage.id);
                announceGame(currentRoll);
              }
            }
          }}
        />
        {!splitRoll && spin && !desk ? (
          <Button onClick={() => wheel.current?.skip()}>{ru.moments.skip}</Button>
        ) : null}
        {staging && !spin && !gameSpin && !heldGenre ? (
          <Button
            onClick={() => {
              setStaged(null);
              // The stage goes with the button: the focus goes to the rolled game
              requestAnimationFrame(() => {
                document.getElementById(rollResultTitle)?.focus();
              });
            }}
          >
            {wheelWords.toMap}
          </Button>
        ) : null}
      </div>
    ) : null;

  // The completion's stage: on a desktop over the map, on a phone in the turn card; it goes when the token stands
  const throwFrom = thrown && mine ? (cellNumber.get(thrown.from) ?? mine.cell) : 0;
  // A later finisher's token stands still: the dice give points only; the frozen first gets no points (free mode)
  const throwStays = mine ? throwFrom === mine.cell : false;
  const throwFree = me?.finish?.frozen === true;
  const finishPoint = board.cells.find((cell) => cell.id === mine?.cell && cell.kind === 'finish');
  const finishCell = finishPoint !== undefined;
  const myActualRow = rows.find((r) => r.player.me);
  const diceStage =
    throwing && thrown && mine ? (
      <CompletionMoment
        key={throwing.id}
        dice={{
          id: 1,
          values: [...throwing.dice, ...throwing.challengeDice].map((d) => d.value),
          challenge: throwing.challengeDice.length,
          sides: [...throwing.dice, ...throwing.challengeDice].map((d) => d.sides),
        }}
        free={throwFree}
        walked={
          thrown.legs
            ? (legsPath(thrown.legs, cellNumber) ?? { path: null, jumps: [] })
            : // On the graph without the server's legs the numbers say nothing about the way: one jump
              graphMode
              ? { path: throwFrom === mine.cell ? null : [throwFrom, mine.cell], jumps: [] }
              : undefined
        }
        board={board}
        players={held?.players ?? players}
        mover={mine}
        from={throwFrom}
        to={mine.cell}
        fill={desk}
        onMove={() => {
          setMovingRun(throwing.id);
        }}
        onArrive={(position) => {
          if (finishCell) arrivedAt.current = position;
        }}
        onDone={(skipped) => {
          setThrown(null);
          setMovingRun(null);
          if (skipped) skipDraw();
          else if (finishCell && me?.finish && !thrown.before.me?.finish) setCelebration('waiting');
          const at = throwFree
            ? ''
            : finishCell
              ? me?.finish?.order === 1
                ? ru.moments.dice.atFirst(myActualRow?.provisional ?? true)
                : me?.finish
                  ? ru.moments.dice.atLater(me.finish.order)
                  : ru.moments.dice.at(mine.cell, true)
              : ru.moments.dice.at(mine.cell, false);
          const plain = throwing.dice.map((d) => d.value);
          const extra = throwing.challengeDice.map((d) => d.value);
          setThrownResult({
            run: throwing.id,
            finishing: finishCell && Boolean(me?.finish) && !thrown.before.me?.finish,
            text: throwFree
              ? ru.moments.dice.afterFree
              : throwStays
                ? ru.moments.dice.afterStay(throwing.total)
                : ru.moments.dice.after(throwing.total, at),
          });
          setAnnounced(
            ru.moments.dice.announce(
              throwing.game.title,
              throwFree
                ? ru.moments.dice.resultFree(plain, extra)
                : throwStays
                  ? ru.moments.dice.resultStay(plain, extra, throwing.total)
                  : ru.moments.dice.result(plain, extra, throwing.total),
              at,
            ),
          );
          // The focus follows only from the moment itself (its skip button) or from nowhere: a player who has
          // moved on to the map or the leaderboard keeps their place
          const active = document.activeElement;
          if (!active || active === document.body || active.closest('[data-testid="dice"]'))
            requestAnimationFrame(() => {
              document
                .getElementById(lastDiceId)
                ?.closest('summary')
                ?.focus({ preventScroll: true });
            });
        }}
      />
    ) : null;
  // The walk of the chosen branch: on a desktop over the map, on a phone in the turn card
  const walkStage =
    moving && mine && walked ? (
      <div
        data-testid="walk"
        className={cx('grid w-full justify-items-center', desk ? 'relative h-full' : 'gap-3')}
      >
        <TokenMove
          key={moving.sequence}
          ref={walk}
          board={board}
          players={walkingHeld?.players ?? players}
          mover={{ ...mine, cell: walked.path[0] ?? mine.cell }}
          path={walked.path}
          jumps={walked.jumps}
          onArrive={(position) => {
            if (finishCell) arrivedAt.current = position;
          }}
          className={cx('w-full', desk ? 'h-full' : 'h-90 rounded-lg border-3 border-ink')}
          onPhase={(phase) => {
            if (phase !== 'done') return;
            setWalking(null);
            if (
              finishCell &&
              me?.finish &&
              !moving.before.me?.finish &&
              me.lastMove?.sequence === moving.sequence &&
              skippedWalk.current !== moving.sequence
            )
              setCelebration('waiting');
            setAnnounced(ru.map.branch.announce(mine.cell));
          }}
        />
        <Button
          className={desk ? 'absolute bottom-4 left-1/2 z-10 -translate-x-1/2' : undefined}
          onClick={() => {
            skippedWalk.current = moving.sequence;
            skipDraw();
            walk.current?.skip();
          }}
        >
          {ru.moments.skip}
        </Button>
      </div>
    ) : null;
  const options =
    branch && !throwing && !moving && graphMode ? branchOptions(branch, season, cellNumber) : null;
  const branchCard =
    branch && options ? (
      <BranchChoice
        wide={desk}
        steps={branch.steps ?? 1}
        options={options}
        pending={pending}
        onChoose={(optionId) => void act({ kind: 'choose', choiceId: branch.id, optionId })}
      />
    ) : null;
  const onMap = desk ? (stage ?? diceStage ?? walkStage) : null;
  const phoneMoment = !desk ? (diceStage ?? walkStage) : null;
  const visibleThrough = beforeMoment?.lastSequence ?? season.lastSequence;

  return (
    <main
      ref={content}
      style={{
        minHeight: !desk && scrollFloor > 0 ? scrollFloor : undefined,
        overflowAnchor: pending || beforeMoment ? 'none' : undefined,
      }}
      className={cx(
        'mx-auto grid max-w-400 grid-cols-1 items-start gap-4 px-4 pt-4 pb-28',
        'desk:season-frame desk:items-stretch desk:overflow-hidden desk:pb-4',
        !desk && scrollFloor > 0 && 'content-start',
      )}
    >
      {celebration === 'playing' && me?.finish && !finished && finishPoint ? (
        <Confetti
          overlay
          origin={() => {
            const arrival = arrivedAt.current;
            if (!desk && inViewport(arrival)) return arrival;
            const current = camera.current?.project(finishPoint) ?? null;
            // A projected point can be inside the viewport while the finish itself is covered or clipped.
            if (
              inViewport(current) &&
              mapFrame.current
                ?.querySelector('svg[role="application"]')
                ?.contains(document.elementFromPoint(current.x, current.y))
            )
              return current;
            if (inViewport(arrival)) return arrival;
            // Use the standard confetti origin when neither map point is visible.
            return { x: window.innerWidth / 2, y: window.innerHeight * 0.4 };
          }}
          onDone={() => {
            setCelebration(null);
          }}
        />
      ) : null}
      <div
        data-testid="season-left"
        className="grid min-w-0 content-start gap-4 desk:min-h-0 desk:overflow-y-auto desk:overscroll-contain desk:px-1"
      >
        <header className="grid min-w-0 gap-2">
          <PageHeader ref={heading} tabIndex={-1} className="text-balance outline-none">
            {season.name}
          </PageHeader>
          <div className="flex flex-wrap gap-2">
            {season.deadline ? (
              <span data-testid="season-deadline">
                <Chip icon={<CalendarClock size={16} aria-hidden />}>
                  {ru.season.deadline(moscowTime(season.deadline))}
                </Chip>
              </span>
            ) : null}
            {me?.coins != null ? (
              <span data-testid="my-coins">
                <Chip icon={<Coins size={16} aria-hidden />}>{ru.season.coins(me.coins)}</Chip>
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
        {season.globalEvents?.length ? (
          <GlobalEventBanner events={season.globalEvents} compact />
        ) : null}
        {loadFailed ? (
          <div className="min-w-0">
            <Notice tone="danger">{ru.app.loadError}</Notice>
          </div>
        ) : null}

        <section
          aria-labelledby="turn-title"
          data-testid="turn"
          className="grid min-w-0 grid-cols-1 gap-4 rounded-lg bg-card p-4"
        >
          <h2 id="turn-title" className="font-display text-lg font-heavy">
            {!me
              ? role === 'player'
                ? ru.turn.outsiderTitle
                : role === 'admin'
                  ? ru.turn.adminOutsideTitle
                  : ru.turn.spectatorTitle
              : finished
                ? ru.turn.finishedTitle
                : closing
                  ? ru.turn.closingTitle
                  : draft
                    ? ru.turn.draftTitle
                    : ru.turn.title}
          </h2>
          {!me && (
            <p className="text-ink-soft">
              {role === 'player'
                ? ru.turn.outsider
                : role === 'admin'
                  ? ru.turn.adminOutside
                  : ru.turn.spectator}
            </p>
          )}
          {me && draft && <p className="text-ink-soft">{ru.turn.draftText}</p>}
          {me && finished && <p className="text-ink-soft">{ru.turn.finishedText}</p>}
          {me && closing && <p className="text-ink-soft">{ru.turn.closingText}</p>}
          {/* D-260: a finisher's season goes on; the card says where he stands and what his next runs give */}
          {me?.finish && !finished && (!beforeMoment || beforeMoment.me?.finish) ? (
            <MyFinish
              finish={me.finish}
              first={rows.find((r) => r.player.me)?.isFirst === true}
              place={rows.find((r) => r.player.me)?.place ?? null}
              rollsOpen={turnsOpen}
              proofPending={finishRun?.proof?.status === 'pending'}
            />
          ) : null}
          {/* A refusal is said at the top of the card, where the eye is after the action */}
          {message && !completing && <Notice tone="danger">{message}</Notice>}
          {/* D-134: at the limit of runs waiting for the admin's check the roll is closed: the step's statement */}
          {rollSlot && uncheckedBlocked ? (
            <Notice tone="warning">
              {ru.turn.uncheckedBlocked(me.unchecked?.count ?? 0, me.unchecked?.limit ?? 0)}
            </Notice>
          ) : null}
          {phoneMoment ? (
            <div
              ref={focusPhoneMoment}
              aria-label={ru.turn.title}
              role="region"
              className={phoneMomentClass}
            >
              {phoneMoment}
            </div>
          ) : null}
          {spinningStatus ? (
            <div className="grid gap-2 justify-items-start">
              <p data-testid="moment-status" role="status" className="text-ink-soft">
                {spinningStatus}
              </p>
              {splitRoll && spin ? (
                <Button
                  data-testid="skip-roll"
                  variant="quiet"
                  disabled={pending}
                  onClick={() => void revealGame(false)}
                >
                  {wheelWords.skipRoll}
                </Button>
              ) : (
                <Button variant="quiet" onClick={() => wheel.current?.skip()}>
                  {ru.moments.skip}
                </Button>
              )}
            </div>
          ) : null}
          {desk && throwing ? (
            <p data-testid="throwing" role="status" className="text-ink-soft">
              {movingRun === throwing.id
                ? ru.moments.dice.moving(0)
                : ru.turn.throwing(throwing.game.title)}
            </p>
          ) : null}
          {drawPending && !throwing && !moving ? (
            <p role="status" className="text-ink-soft">
              {ru.moments.dice.drawing}
            </p>
          ) : null}
          {manualFirst && !throwing ? afterRun : null}
          {waiting && (
            <div className="grid gap-2" aria-busy="true">
              <Skeleton className="h-6 w-2/3" />
              <Skeleton className="h-12 w-full" />
            </div>
          )}
          {branchCard ? (
            desk ? (
              <p data-testid="branch-hint" className="font-bold">
                {ru.map.branch.onMap}
              </p>
            ) : (
              branchCard
            )
          ) : null}
          {mainProof ? (
            <Button
              variant="main"
              data-testid="send-proof"
              aria-controls={`proof-${mainProof.id}`}
              disabled={pending}
              onClick={() => {
                openProof(mainProof.id);
              }}
            >
              {finishProof ? ru.proof.submit : ru.turn.sendProof}
            </Button>
          ) : null}
          {rollSlot && !uncheckedBlocked && (
            <div className="grid gap-2">
              <p className="text-sm text-ink-soft">{wheelWords.genreStep}</p>
              <Button
                variant={finishProof || (drawPending && me.finish) ? 'quiet' : 'main'}
                data-testid="roll"
                loading={pending}
                disabled={pending}
                onClick={() => void act({ kind: 'roll' })}
              >
                {ru.turn.roll}
              </Button>
              {me.unchecked && me.unchecked.count > 0 ? (
                <p className="text-sm text-ink-soft" data-testid="unchecked">
                  {ru.turn.uncheckedWaiting(me.unchecked.count, me.unchecked.limit)}
                </p>
              ) : null}
            </div>
          )}
          {splitRoll && heldGenre && !spin ? (
            <RollGenre
              category={splitRoll.category}
              games={gameWheel?.sectors.length ?? 0}
              choice={choice !== null}
              opening={openingGame || pending}
              onOpen={() => void revealGame(true)}
              onSkip={() => void revealGame(false)}
            />
          ) : null}
          {desk ? null : stage}
          {effectChoice && !throwing && !moving ? (
            <div className={cx('grid min-w-0 gap-3', drawStageClass)}>
              <EventChoice
                choice={effectChoice}
                busy={pending}
                disabled={!answerAllowed}
                drawn={me?.lastEvent ?? null}
                fresh={freshDraw}
                onShown={noteDrawShown}
                onSettled={newDraw ? setSettledDraw : undefined}
                unavailableReason={
                  answerAllowed
                    ? ''
                    : finished
                      ? ru.events.choice.closed
                      : draft
                        ? ru.events.choice.draft
                        : ru.events.choice.paused
                }
                onChoose={(optionId) =>
                  void act({ kind: 'choose', choiceId: effectChoice.id, optionId })
                }
              />
              {closeDraw}
            </div>
          ) : null}
          {choice && showRollResult && (
            <ChoiceCard
              choice={choice}
              className={rollRevealClass}
              eventsEnabled={me?.eventsEnabled === true}
              roll={me?.roll ?? null}
              price={me?.nextReroll ?? null}
              coins={me?.coins ?? null}
              pending={pending}
              onChoose={(optionId, difficulty) =>
                void act({ kind: 'choose', choiceId: choice.id, optionId, difficulty })
              }
              onAlreadyPlayed={(gameId) => void act({ kind: 'alreadyPlayed', gameId })}
              onReroll={() =>
                void act({ kind: 'reroll', expected: me?.nextReroll?.payment ?? null })
              }
            />
          )}
          {offer && showRollResult && (
            <OfferCard
              offer={offer}
              className={rollRevealClass}
              eventsEnabled={me?.eventsEnabled === true}
              roll={me?.roll ?? null}
              price={me?.nextReroll ?? null}
              coins={me?.coins ?? null}
              pending={pending}
              onStart={(difficulty) => void act({ kind: 'start', gameId: offer.id, difficulty })}
              onAlreadyPlayed={() => void act({ kind: 'alreadyPlayed', gameId: offer.id })}
              onReroll={() =>
                void act({ kind: 'reroll', expected: me?.nextReroll?.payment ?? null })
              }
            />
          )}
          {/* The result is said once, when the wheel stops, and outlives the wheel */}
          <p className="sr-only" aria-live="polite" data-testid="roll-announce">
            {announced}
          </p>
          {/* A card drawn while the page is open is said once, when it is drawn on screen */}
          <p className="sr-only" aria-live="polite" data-testid="draw-announce">
            {newDraw && !drawPending && !moving && !throwing && me?.lastEvent
              ? ru.events.announce(me.lastEvent.kind, me.lastEvent.name)
              : ''}
          </p>
          {me?.phase === 'playing' && me.activeRun && (
            <div data-testid="active-run" className="grid gap-4">
              <p className="sr-only">{ru.turn.playing(me.activeRun.game.title)}</p>
              <RunCard
                game={{
                  title: me.activeRun.game.title,
                  hours: me.activeRun.game.hours ?? null,
                  tags: [],
                  cover: me.activeRun.game.cover?.thumbnailUrl,
                }}
                left={myRow ? myRow.cellsToFinish : routeLength}
                level={3}
                total={routeLength}
                actions={null}
                compact
              />
              {me.isCoopPartner ? (
                <p className="text-ink-soft">{ru.turn.coopPartnerWaiting}</p>
              ) : null}
              {me.eventsEnabled === true ? (
                <RunConditions conditions={me.activeRun.conditions ?? null} />
              ) : null}
              {me.activeDuels?.length ? (
                <ul data-testid="run-duels" className="grid gap-2 text-sm">
                  {me.activeDuels.map((duel) => {
                    const opponentId =
                      duel.challengerId === me.playerId ? duel.opponentId : duel.challengerId;
                    const opponent = season.players.find((player) => player.id === opponentId);
                    return opponent ? (
                      <li key={duel.id} className="flex items-start gap-2 rounded-md bg-muted p-3">
                        <Swords size={18} aria-hidden className="shrink-0" />
                        <span>{ru.turn.race(opponent.name, moscowTime(duel.expiresAt))}</span>
                      </li>
                    ) : null;
                  })}
                </ul>
              ) : null}
            </div>
          )}
          {me && completableRun && (
            <div key={completionKey} className="grid min-w-0 gap-4">
              <Button
                ref={completionOpener}
                data-testid="complete-open"
                variant="main"
                disabled={pending}
                onClick={() => {
                  setMessage(null);
                  setCompletionDraftRun(completionKey);
                }}
              >
                {ru.turn.completeOpen}
              </Button>
              <CompletionRulesLink className="justify-self-start" />
              <FormDialog
                open={completing}
                onOpenChange={(open) => {
                  if (!open && !pending) setCompletionDraftRun(null);
                }}
                title={ru.turn.completeTitle}
                returnFocus={completionOpener}
                testId="complete-dialog"
              >
                {message ? <Notice tone="danger">{message}</Notice> : null}
                <CompleteForm
                  needsHours={completableRun.game.hours == null}
                  challengesEnabled={me.challengesEnabled}
                  dice={me.difficultyDice ?? null}
                  declared={completableRun.declaredDifficulty ?? null}
                  showHeading={false}
                  pending={pending}
                  proofDraft={{
                    shot: preparedShots.get(runDraftKey(season, completableRun.id)),
                    onChange: (shot) => {
                      setPreparedShot(completableRun.id, shot);
                    },
                  }}
                  onComplete={(completion) => {
                    void act({ kind: 'complete', completion }).then((ok) => {
                      if (ok) setCompletionDraftRun(null);
                    });
                  }}
                />
              </FormDialog>
              <RunActions
                game={completableRun.game.title}
                dropHintMinutes={me.dropHintMinutes}
                dropPenalty={me.dropPenalty}
                techRerollOpen={me.techRerollOpen}
                techRerollUntil={me.techRerollUntil ?? null}
                wishRerollOpen={me.wishRerollOpen === true}
                onWishReroll={() => void act({ kind: 'techReroll', reason: 'wish', comment: null })}
                frozen={me.finish?.frozen === true}
                pending={pending}
                onDrop={() => {
                  void act({ kind: 'drop', runId: completableRun.id });
                }}
                onTechReroll={(reason, comment) =>
                  void act({ kind: 'techReroll', reason, comment })
                }
              />
            </div>
          )}
          {me?.eventsEnabled === true ? (
            <WaitingEvents
              events={me.waitingEvents ?? null}
              conditions={me.waitingConditions ?? null}
              compact
            />
          ) : null}
          {irlEffects.length > 0 ? (
            <section
              aria-labelledby="irl-proofs-title"
              data-testid="irl-effects"
              className={cx('min-w-0 gap-3', throwing || moving ? 'hidden' : 'grid')}
              hidden={Boolean(throwing || moving)}
            >
              <h3 id="irl-proofs-title" className="font-display font-heavy">
                {ru.irlProof.tasks}
              </h3>
              <ul className="grid min-w-0 gap-4">
                {irlEffects.map((effect) => (
                  <li
                    key={effect.id}
                    data-testid={`manual-effect-${effect.id}`}
                    className="grid min-w-0 gap-2"
                  >
                    <IrlProofSection
                      effect={effect}
                      maxFiles={me?.irlProofMaxFiles ?? 0}
                      pending={pending}
                      enabled={answerAllowed}
                      unavailableReason={
                        answerAllowed
                          ? ''
                          : finished
                            ? ru.irlProof.closed
                            : draft
                              ? ru.irlProof.draft
                              : ru.irlProof.paused
                      }
                      onSubmit={(files, link) =>
                        void act({ kind: 'irlProof', effectId: effect.id, files, link })
                      }
                    />
                  </li>
                ))}
              </ul>
            </section>
          ) : null}
          {!manualFirst && !throwing ? afterRun : null}
          {showInteractions ? (
            <Link
              to={paths.interactions(season.id)}
              className={standaloneLink}
              data-testid="season-interactions"
            >
              {ru.turn.interactions}
            </Link>
          ) : null}
        </section>
        {finished && season.nominationsEnabled === true ? (
          <NominationResults seasonId={season.id} onSignedOut={onSignedOut} />
        ) : null}
      </div>

      <section
        aria-labelledby="map-title"
        className="grid min-w-0 gap-2 desk:min-h-0 desk:grid-rows-[minmax(0,1fr)_auto]"
      >
        <h2 id="map-title" className="sr-only">
          {ru.map.title}
        </h2>
        <div ref={mapFrame} className="relative">
          <MapView
            ref={camera}
            board={board}
            players={shownPlayers}
            focus={shownMine && shownMine.cell >= 0 ? shownMine.cell : undefined}
            options={options?.map((o) => o.cell)}
            className={cx(
              'rounded-lg border-3 border-ink desk:h-full',
              phoneMoment ? 'h-105' : 'h-55',
            )}
          />
          {desk && branchCard && !onMap ? (
            <div className="absolute inset-x-3 bottom-3 z-10 rounded-lg border-3 border-ink bg-card p-4 shadow-lift">
              {branchCard}
            </div>
          ) : null}
          {onMap ? (
            <div
              className={cx(
                'absolute inset-0 z-10 grid overflow-hidden rounded-lg border-3 border-ink bg-card',
                stage ? 'p-4' : undefined,
              )}
            >
              {onMap}
            </div>
          ) : null}
        </div>
        <div className="flex flex-wrap items-center justify-between gap-x-4">
          {!desk ? (
            <BottomSheet
              title={ru.map.title}
              full
              trigger={
                <Button variant="quiet" data-testid="map-open">
                  {ru.season.mapOpen}
                </Button>
              }
            >
              <MapView
                board={board}
                players={shownPlayers}
                focus={shownMine && shownMine.cell >= 0 ? shownMine.cell : undefined}
                options={options?.map((option) => option.cell)}
                className="h-full min-h-0 rounded-lg border-3 border-ink"
              />
            </BottomSheet>
          ) : null}
          {graphMode ? (
            <>
              <Button
                ref={legendOpener}
                variant="link"
                onClick={() => {
                  setLegendOpen(true);
                }}
              >
                {ru.map.legend.title}
              </Button>
              <FormDialog
                open={legendOpen}
                onOpenChange={setLegendOpen}
                title={ru.map.legend.title}
                returnFocus={legendOpener}
                wide
              >
                <MapLegend
                  zones={season.zones}
                  kinds={new Set(season.cells.map((cell) => cell.type))}
                  myZone={
                    season.cells.find(
                      (cell) =>
                        cell.id === season.players.find((p) => p.id === me?.playerId)?.cellId,
                    )?.zone
                  }
                />
              </FormDialog>
            </>
          ) : null}
        </div>
        {/* The map in words: every cell and who stands there (also what the tests and screen readers read) */}
        <ol data-testid="cells" className="sr-only">
          {[...season.cells]
            .sort((a, b) => (cellNumber.get(a.id) ?? 0) - (cellNumber.get(b.id) ?? 0))
            .map((cell) => {
              const here = shownPlayers.filter((player) => player.cell === cellNumber.get(cell.id));
              return (
                <li key={cell.id} data-testid={`cell-${cell.id}`}>
                  {cell.type === 'start'
                    ? ru.map.start
                    : cell.type === 'finish'
                      ? ru.map.finish
                      : graphMode
                        ? cellWords(cell, season, cellNumber)
                        : ru.map.cellNumber(cellNumber.get(cell.id) ?? 0)}
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

      <div
        data-testid="season-right"
        className="grid min-w-0 content-start gap-4 desk:min-h-0 desk:overflow-y-auto desk:overscroll-contain desk:px-1"
      >
        <Panel title={ru.leaderboard.title} className="hidden desk:grid">
          <Leaderboard rows={topRows} />
          <Button
            ref={leadersOpener}
            variant="link"
            className="justify-self-start"
            onClick={() => {
              setLeadersOpen(true);
            }}
          >
            {ru.leaderboard.full}
          </Button>
          <FormDialog
            open={leadersOpen}
            onOpenChange={setLeadersOpen}
            title={ru.leaderboard.title}
            returnFocus={leadersOpener}
            wide
          >
            <p className="text-sm text-ink-soft">{ru.board.rule}</p>
            <div ref={showMe}>
              <Leaderboard rows={shownRows} marked={false} />
            </div>
          </FormDialog>
        </Panel>
        {desk ? (
          <FeedPreview
            seasonId={seasonId}
            version={visibleThrough}
            visibleThrough={visibleThrough}
            hiddenRollSequence={splitRoll && !showRollResult ? splitRoll.sequence : undefined}
          />
        ) : null}
        {me && (season.economy?.items || season.economy?.shop || season.economy?.bets) ? (
          <EconomySection
            seasonId={seasonId}
            version={season.lastSequence}
            onSignedOut={onSignedOut}
            items={season.economy.items}
            shop={season.economy.shop}
            bets={season.economy.bets}
            compact
          />
        ) : null}
        <PlayerSuggestions
          seasonId={seasonId}
          role={role}
          participating={me !== null}
          seasonStatus={season.status}
          onSignedOut={onSignedOut}
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
              {shownMine ? (
                <Sticker player={shownMine} size={36} />
              ) : (
                <Trophy size={24} aria-hidden />
              )}
              <span className="grid">
                <strong className="font-display">{ru.leaderboard.title}</strong>
                {myRow && leader ? (
                  <span className="text-sm text-ink-soft">
                    {leaderTied && myRow.place === 1
                      ? ru.board.sheetShared(myRow.points)
                      : leader.player.me || leaderTied || myRow.place === 1
                        ? ru.board.sheet(myRow.place, myRow.points)
                        : ru.board.sheetPeek(leader.player.name, myRow.place)}
                  </span>
                ) : leader ? (
                  <span className="text-sm text-ink-soft">
                    {leaderTied ? ru.board.sheetNoLeader : ru.board.sheetLeader(leader.player.name)}
                  </span>
                ) : null}
              </span>
            </button>
          }
        >
          <p className="text-sm text-ink-soft">{ru.board.rule}</p>
          <div ref={showMe}>
            <Leaderboard rows={shownRows} marked={false} />
          </div>
        </BottomSheet>
      </div>
    </main>
  );
}

/** A cell of the graph in words: its number, what it does, its zone */
function cellWords(
  cell: Schemas['CellView'],
  season: Season,
  cellNumber: Map<string, number>,
): string {
  const kind = ru.map.cellKinds[cell.type] ?? '';
  const zone = season.zones.find((z) => z.id === cell.zone)?.name;
  return [
    ru.map.cellNumber(cellNumber.get(cell.id) ?? 0),
    kind,
    cell.type === 'teleport' && cell.to ? ru.map.teleportTo(cellNumber.get(cell.to) ?? 0) : '',
    cell.type === 'pointsBonus' && cell.amount ? ru.map.bonusAmount(cell.amount) : '',
    zone ? ru.map.inZone(zone) : '',
  ]
    .filter(Boolean)
    .join(', ');
}

const rollStepLifetime = 10 * 60 * 1000;

function freshRoll(roll: Schemas['WheelRollView']) {
  return roll.rolledAt != null && Date.now() - Date.parse(roll.rolledAt) < rollStepLifetime;
}

function rollStorageKey(seasonId: string, playerId: string, sequence: number) {
  return `ivent.roll.shown:${seasonId}:${playerId}:${String(sequence)}`;
}

function readRollMarker(key: string, revealId: string) {
  try {
    return localStorage.getItem(key) === revealId;
  } catch {
    return false;
  }
}

function writeRollMarker(key: string, revealId: string) {
  try {
    localStorage.setItem(key, revealId);
  } catch {
    // The current page still remembers the step when this browser blocks storage.
  }
}

function genreWheelRoll(roll: Schemas['WheelRollView']): WheelRoll {
  return {
    id: roll.sequence,
    misses: [],
    pick: {
      sector: Math.max(roll.sectors.indexOf(roll.category), 0),
      game: { title: roll.category },
    },
  };
}

function gameWheelRoll(roll: Schemas['WheelRollView'], offer: Schemas['OfferedGameView']) {
  const games = roll.wheelGames ?? [];
  const sector = games.findIndex((game) => game.id === offer.id);
  if (sector < 0) return null;
  const misses = roll.misses.flatMap((miss) => {
    const stop = games.findIndex((game) => game.id === miss.gameId);
    return stop < 0
      ? []
      : [
          {
            sector: stop,
            game: { title: miss.game },
            by: miss.player,
            playing: miss.reason === 'beingPlayed',
          },
        ];
  });
  return {
    sectors: games.map((game) => game.title),
    roll: {
      id: roll.sequence,
      misses,
      unshownMisses: Math.max(0, (roll.missCount ?? roll.misses.length) - misses.length),
      pick: { sector, game: { title: offer.title, cover: offer.cover?.thumbnailUrl } },
    } satisfies WheelRoll,
  };
}

/** Versioned older rolls already include their misses on the genre wheel. */
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
      ? { sector, game: { title: offer.title, cover: offer.cover?.thumbnailUrl } }
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
      <p className="sr-only">{ru.ui.loading}</p>
      <div className="grid content-start gap-4 desk:w-96">
        <Skeleton className="h-8 w-2/3" />
        <Skeleton className="h-48 w-full rounded-lg" />
      </div>
      <Skeleton className="h-105 w-full rounded-lg desk:h-190" />
    </main>
  );
}

/** Describe the observed result: an ignored condition can still have dice; a rejected proof has separate penalties. */
function conditionResultNote(run: Schemas['CompletedRunView']): string | null {
  return run.status === 'completed' &&
    run.conditionCheck?.met === false &&
    !run.conditionCheck.waived &&
    run.dice.length === 0 &&
    run.challengeDice.length === 0
    ? ru.runConditions.noDiceResult
    : null;
}
