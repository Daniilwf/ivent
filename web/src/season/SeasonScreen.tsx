import { useCallback, useEffect, useRef, useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { watchSeason } from '../api/realtime';
import { ru } from '../i18n/ru';
import { CompleteForm, type Completion } from './CompleteForm';
import { RerollButton } from './RerollButton';
import { GameMarks } from './GameMarks';
import { RunActions } from './RunActions';

type Season = Schemas['SeasonView'];
type Command =
  | { kind: 'roll' }
  | { kind: 'start' }
  | { kind: 'reroll' }
  | { kind: 'drop' }
  | { kind: 'techReroll'; reason: NonNullable<Schemas['TechRerollReason']>; comment: string | null }
  | { kind: 'complete'; completion: Completion }
  | { kind: 'choose'; choiceId: string; optionId: string }
  | { kind: 'alreadyPlayed'; gameId: string };
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

  // Load now, after every committed command of the season (another player's move included) and after
  // every (re)connection to the hub.
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

  if (loadFailed && !season) return <p role="alert">{ru.app.loadError}</p>;
  if (!season) return <p>{ru.app.loading}</p>;

  const me = season.me;
  const waiting =
    (me?.phase === 'rolling' && !me.offer && !me.choice) ||
    (me?.phase === 'playing' && !me.activeRun);
  const choice = me?.phase === 'rolling' ? me.choice : null;
  const offer = me?.phase === 'rolling' ? me.offer : null;
  return (
    <main>
      <h1>{ru.app.title}</h1>
      {loadFailed && <p role="alert">{ru.app.loadError}</p>}
      <section aria-labelledby="turn-title" data-testid="turn">
        <h2 id="turn-title">{ru.turn.title}</h2>
        {!me && <p>{ru.turn.spectator}</p>}
        {waiting && <p>{ru.app.loading}</p>}
        {me?.phase === 'idle' && (
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
          <>
            <p data-testid="active-run">{ru.turn.playing(me.activeRun.game.title)}</p>
            <CompleteForm
              needsHours={me.activeRun.game.hours == null}
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
                <li key={effect.id} data-testid={`manual-effect-${effect.id}`}>
                  {ru.effects.drawEvent(effect.drawEvent, effect.source)}
                </li>
              ))}
            </ul>
          </section>
        )}
        {me?.lastCompleted && (
          <>
            <p data-testid="last-dice">
              {ru.turn.lastDice(
                me.lastCompleted.game.title,
                me.lastCompleted.dice.map((d) => d.value),
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
          </>
        )}
      </section>

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
        {/* Stage 1 slice: by points only; the first finisher on top comes with task C9. */}
        <ol data-testid="leaderboard">
          {[...season.players]
            .sort((a, b) => b.points - a.points)
            .map((p) => (
              <li key={p.id} data-testid={`leader-${p.id}`}>
                {ru.leaderboard.row(p.name, p.points)}
              </li>
            ))}
        </ol>
      </section>
    </main>
  );
}
