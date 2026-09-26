import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { watchSeason } from '../api/realtime';
import { ru } from '../i18n/ru';
import { CompleteForm, type Completion } from './CompleteForm';
import { RerollButton } from './RerollButton';
import { GameMarks } from './GameMarks';
import { AvatarSection } from './AvatarSection';
import { ProofSection } from './ProofForm';
import { ManualEffectItem, type EffectOutcome } from './ManualEffectItem';
import { RunActions } from './RunActions';

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

/** The slice screen (stage 1, no design yet): my turn, the cell line with tokens, the players. */
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

  const apply = useCallback(
    (loaded: Loaded) => {
      if (loaded.kind === 'signedOut') {
        onSignedOut();
      } else if (loaded.kind === 'failed') {
        setLoadFailed(true);
      } else if (loaded.season.lastSequence >= lastSequence.current) {
        lastSequence.current = loaded.season.lastSequence;
        setSeason(loaded.season);
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

  if (loadFailed && !season) return <p role="alert">{ru.app.loadError}</p>;
  if (!season) return <p>{ru.app.loading}</p>;

  const me = season.me;
  const waiting =
    (me?.phase === 'rolling' && !me.offer && !me.choice) ||
    (me?.phase === 'playing' && !me.activeRun);
  const turnsOpen = season.status === 'active' && !pastDeadline;
  const choice = turnsOpen && me?.phase === 'rolling' ? me.choice : null;
  const offer = turnsOpen && me?.phase === 'rolling' ? me.offer : null;
  return (
    <main>
      <h1>{ru.app.title}</h1>
      {loadFailed && <p role="alert">{ru.app.loadError}</p>}
      {season.deadline && (
        <p data-testid="season-deadline">{ru.season.deadline(moscowTime(season.deadline))}</p>
      )}
      {(season.status === 'closing' || (season.status === 'active' && pastDeadline)) && (
        <p data-testid="season-status">{ru.season.closing}</p>
      )}
      {(season.status === 'finished' || season.status === 'archived') && (
        <p data-testid="season-status">{ru.season.finished}</p>
      )}
      <section aria-labelledby="turn-title" data-testid="turn">
        <h2 id="turn-title">{ru.turn.title}</h2>
        {!me && <p>{ru.turn.spectator}</p>}
        {waiting && <p>{ru.app.loading}</p>}
        {me?.phase === 'idle' && turnsOpen && (
          <button data-testid="roll" disabled={pending} onClick={() => void act({ kind: 'roll' })}>
            {ru.turn.roll}
          </button>
        )}
        {choice && (
          <fieldset data-testid="choice">
            <legend>{ru.turn.choose}</legend>
            {choice.options
              .flatMap(({ id, game }) => (game ? [{ id, game }] : []))
              .map((option) => (
                <button
                  key={option.id}
                  data-testid={`option-${option.id}`}
                  disabled={pending}
                  onClick={() =>
                    void act({ kind: 'choose', choiceId: choice.id, optionId: option.id })
                  }
                >
                  {ru.turn.option(option.game.title, option.game.hours ?? null)}
                  <GameMarks marks={option.game.marks} />
                </button>
              ))}
            {choice.options.map(({ id, game }) =>
              game ? (
                <button
                  key={`played-${id}`}
                  data-testid={`already-played-${id}`}
                  disabled={pending}
                  onClick={() => void act({ kind: 'alreadyPlayed', gameId: game.id })}
                >
                  {ru.turn.alreadyPlayedGame(game.title)}
                </button>
              ) : null,
            )}
            <RerollButton
              price={me?.nextReroll ?? null}
              pending={pending}
              onReroll={() => void act({ kind: 'reroll' })}
            />
          </fieldset>
        )}
        {offer && (
          <>
            <p data-testid="offer">{ru.turn.offered(offer.title, offer.hours ?? null)}</p>
            <GameMarks marks={offer.marks} />
            <button
              data-testid="start"
              disabled={pending}
              onClick={() => void act({ kind: 'start' })}
            >
              {ru.turn.start}
            </button>
            <button
              data-testid="already-played"
              disabled={pending}
              onClick={() => void act({ kind: 'alreadyPlayed', gameId: offer.id })}
            >
              {ru.turn.alreadyPlayed}
            </button>
            <RerollButton
              price={me?.nextReroll ?? null}
              pending={pending}
              onReroll={() => void act({ kind: 'reroll' })}
            />
          </>
        )}
        {me?.phase === 'playing' && me.activeRun && (
          <p data-testid="active-run">{ru.turn.playing(me.activeRun.game.title)}</p>
        )}
        {turnsOpen && me?.phase === 'playing' && me.activeRun && (
          <>
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
          </>
        )}
        {message && <p role="alert">{message}</p>}
        {me && me.manualEffects.length > 0 && (
          <section aria-labelledby="effects-title" data-testid="manual-effects">
            <h3 id="effects-title">{ru.effects.title}</h3>
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
        {me?.lastCompleted && (
          <>
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
          </>
        )}
      </section>

      <AvatarSection
        onChanged={() => {
          void fetchSeason(seasonId).then(apply);
        }}
      />

      <section aria-labelledby="map-title">
        <h2 id="map-title">{ru.map.title}</h2>
        <ol
          data-testid="cells"
          style={{ display: 'flex', flexWrap: 'wrap', listStyle: 'none', gap: 4, padding: 0 }}
        >
          {season.cells.map((cell) => {
            const here = season.players.filter((p) => p.cellId === cell.id);
            return (
              <li key={cell.id} data-testid={`cell-${cell.id}`}>
                {cell.type === 'start'
                  ? ru.map.start
                  : cell.type === 'finish'
                    ? ru.map.finish
                    : ru.map.cell}
                {here.map((p) => (
                  <span key={p.id} data-testid={`token-${p.id}`}>
                    {' '}
                    {p.avatar && <img src={p.avatar.thumbnailUrl} alt="" width={20} height={20} />}
                    {p.name}
                  </span>
                ))}
              </li>
            );
          })}
        </ol>
      </section>

      <section aria-labelledby="leaders-title">
        <h2 id="leaders-title">{ru.leaderboard.title}</h2>
        {/* In the server's place order (D-100): the first finisher on top whatever the points. */}
        <ol data-testid="leaderboard">
          {season.leaderboard.map((row) => (
            <li key={row.playerId} data-testid={`leader-${row.playerId}`}>
              {ru.leaderboard.row(
                row.place,
                season.players.find((p) => p.id === row.playerId)?.name ?? '',
                row.points,
                row.cellsToFinish ?? null,
              )}
              {row.isFirst &&
                ` ${row.provisional ? ru.leaderboard.provisional : ru.leaderboard.first}`}
            </li>
          ))}
        </ol>
      </section>
    </main>
  );
}

/** A UTC instant as the Moscow date and time: deadlines are shown in Moscow time with an explicit label (SPEC). */
function moscowTime(utc: string): string {
  return new Date(utc).toLocaleString('ru-RU', {
    timeZone: 'Europe/Moscow',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
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
