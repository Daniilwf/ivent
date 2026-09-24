import { useCallback, useEffect, useRef, useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { watchSeason } from '../api/realtime';
import { ru } from '../i18n/ru';
import { CompleteForm, type Completion } from './CompleteForm';

type Season = Schemas['SeasonView'];
type Action = 'roll' | 'start' | 'complete';
type Loaded = { kind: 'season'; season: Season } | { kind: 'signedOut' } | { kind: 'failed' };

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

  async function act(action: Action, completion?: Completion) {
    setPending(true);
    setMessage(null);
    try {
      // One command id per action: a retried request acts once (D-68).
      const commandId = crypto.randomUUID();
      const params = { path: { seasonId } };
      const result =
        action === 'complete' && completion
          ? await api.POST('/api/seasons/{seasonId}/complete', {
              params,
              body: { commandId, ...completion },
            })
          : action === 'roll'
            ? await api.POST('/api/seasons/{seasonId}/roll', { params, body: { commandId } })
            : await api.POST('/api/seasons/{seasonId}/start', { params, body: { commandId } });
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
    (me?.phase === 'rolling' && !me.offer) || (me?.phase === 'playing' && !me.activeRun);
  return (
    <main>
      <h1>{ru.app.title}</h1>
      {loadFailed && <p role="alert">{ru.app.loadError}</p>}
      <section aria-labelledby="turn-title" data-testid="turn">
        <h2 id="turn-title">{ru.turn.title}</h2>
        {!me && <p>{ru.turn.spectator}</p>}
        {waiting && <p>{ru.app.loading}</p>}
        {me?.phase === 'idle' && (
          <button data-testid="roll" disabled={pending} onClick={() => void act('roll')}>
            {ru.turn.roll}
          </button>
        )}
        {me?.phase === 'rolling' && me.offer && (
          <>
            <p data-testid="offer">{ru.turn.offered(me.offer.title, me.offer.hours ?? null)}</p>
            <button data-testid="start" disabled={pending} onClick={() => void act('start')}>
              {ru.turn.start}
            </button>
          </>
        )}
        {me?.phase === 'playing' && me.activeRun && (
          <>
            <p data-testid="active-run">{ru.turn.playing(me.activeRun.game.title)}</p>
            <CompleteForm
              needsHours={me.activeRun.game.hours == null}
              pending={pending}
              onComplete={(completion) => void act('complete', completion)}
            />
          </>
        )}
        {message && <p role="alert">{message}</p>}
        {me?.lastCompleted && (
          <p data-testid="last-dice">
            {ru.turn.lastDice(
              me.lastCompleted.game.title,
              me.lastCompleted.dice.map((d) => d.value),
              me.lastCompleted.total,
            )}
          </p>
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
