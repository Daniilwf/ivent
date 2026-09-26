import { CalendarClock } from 'lucide-react';
import { ru } from '../../i18n/ru';
import type { Content, Player } from './content';
import { graph } from './graph';
import { MapView, type MapVariant } from './MapView';

const t = ru.designPreview;
const finishDistance = (p: Player) => Math.max(graph.cells.length - p.cell, 0);

export function Sticker({ player, size }: { player: Player; size: number }) {
  return (
    <span
      className="sticker"
      style={{
        width: size,
        height: size,
        background: player.color,
        color: player.ink,
        fontSize: size * 0.42,
      }}
    >
      {player.gif ? <img src={player.gif} alt="" width={size} height={size} /> : player.name[0]}
    </span>
  );
}

export function Leaderboard({ players, limit }: { players: Player[]; limit?: number }) {
  return (
    <ol className="board-list">
      {(limit ? players.slice(0, limit) : players).map((p, i) => (
        <li key={p.id} className={p.me ? 'is-me' : undefined}>
          <span className="pos">{i + 1}</span>
          <Sticker player={p} size={36} />
          <span className="nick" title={p.name}>
            <span className="nick-line">
              <span className="nick-name">{p.name}</span>
              {p.first ? <span className="badge">{t.first}</span> : null}
              {p.me ? <span className="badge">{t.you}</span> : null}
            </span>
            <span className="sub">{p.inactive ? t.inactive : t.toFinish(finishDistance(p))}</span>
          </span>
          <span className="pts">{p.points}</span>
        </li>
      ))}
    </ol>
  );
}

export function MainScreen({
  content,
  variant,
  compact,
}: {
  content: Content;
  variant: MapVariant;
  compact?: boolean;
}) {
  const { players, games } = content;
  const me = players.find((p) => p.me) as Player;
  // The run on screen has the longest title of the pool: the layout must hold it
  const run = games[1] ?? games[0];
  return (
    <div className={`main ${compact ? 'compact' : ''}`}>
      <header className="top">
        <span className="season">{t.season}</span>
        <span className="chip">
          <CalendarClock size={16} aria-hidden />
          {t.daysLeft(5)}
        </span>
      </header>

      {run ? (
        <section className="run" aria-label={t.nowPlaying}>
          {run.cover ? (
            <img className="run-cover" src={run.cover} alt="" width={96} height={144} />
          ) : (
            <span className="run-cover" />
          )}
          <div>
            <p className="run-label">{t.nowPlaying}</p>
            <h1 className="run-title" title={run.title}>
              {run.title}
            </h1>
            <p className="run-label">{t.hours(run.hours)}</p>
            <div className="tags">
              {run.tags.map((tag) => (
                <span key={tag}>{tag}</span>
              ))}
            </div>
          </div>
          <div className="run-actions">
            <button type="button" className="btn-main">
              {t.complete}
            </button>
            <button type="button" className="btn-link">
              {t.drop}
            </button>
          </div>
        </section>
      ) : null}

      <MapView
        variant={variant}
        players={players}
        className="main-map"
        focus={compact ? me.cell : undefined}
      />

      <aside className="side desk-only">
        <section className="panel" aria-label={t.leaderboard}>
          <h2>{t.leaderboard}</h2>
          <Leaderboard players={players} />
        </section>
        <section className="panel" aria-label={t.feed}>
          <h2>{t.feed}</h2>
          <ul className="feed">
            {t.feedItems.map((item, i) => {
              const who = players[item.who] as Player;
              const game = games[item.game];
              return (
                <li key={i}>
                  <Sticker player={who} size={30} />
                  <div className="bubble">
                    <span>
                      <strong>{who.name}</strong> {item.what} {game ? <em>{game.title}</em> : null}
                      {'dice' in item ? ` ${item.dice.join(' + ')}` : null}
                    </span>
                    {game?.wide ? <img src={game.wide} alt="" width={220} height={103} /> : null}
                    <time>{t.ago[i]}</time>
                  </div>
                </li>
              );
            })}
          </ul>
        </section>
      </aside>

      <button type="button" className="sheet phone-only">
        <span className="handle" />
        <span className="sheet-row">
          <Sticker player={me} size={36} />
          <span>
            <strong>{t.leaderboard}</strong>
            <span className="sub">{t.sheet(3, me.points)}</span>
          </span>
        </span>
      </button>
    </div>
  );
}
