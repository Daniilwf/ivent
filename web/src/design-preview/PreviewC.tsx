import '@fontsource/unbounded/cyrillic-600.css';
import '@fontsource/unbounded/cyrillic-800.css';
import '@fontsource/unbounded/latin-600.css';
import '@fontsource/unbounded/latin-800.css';
import '@fontsource/onest/cyrillic-400.css';
import '@fontsource/onest/cyrillic-600.css';
import '@fontsource/onest/latin-400.css';
import '@fontsource/onest/latin-600.css';
import './preview-c.css';
import { ru } from '../i18n/ru';
import { place, playersOn } from './Board';
import { cells, feed, me, players, season, type PreviewPlayer } from './data';

const t = ru.designPreview;

// C — «Компания»: the site as the friends' own chat. Players are stickers with a thick white edge, the feed reads like
// messages, the map is a route drawn with a marker. Unbounded for headings and numbers, Onest for the rest.

function Sticker({ player, size }: { player: PreviewPlayer; size: number }) {
  return (
    <span
      className="c-sticker"
      style={{ width: size, height: size, background: player.color, fontSize: size * 0.45 }}
      title={player.name}
    >
      {player.name[0]}
    </span>
  );
}

function Route({ columns, className }: { columns: number; className: string }) {
  const step = 56;
  const width = columns * step;
  const height = Math.ceil(cells / columns) * step;
  const at = (n: number) => {
    const { row, column } = place(n, columns);
    return { x: column * step + step / 2, y: row * step + step / 2 };
  };
  const path = Array.from({ length: cells }, (_, i) => at(i + 1))
    .map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x} ${p.y}`)
    .join(' ');
  return (
    <svg
      className={`c-route ${className}`}
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={t.map}
    >
      <path d={path} className="c-line" />
      {Array.from({ length: cells }, (_, i) => i + 1).map((n) => {
        const { x, y } = at(n);
        const here = playersOn(n);
        const mine = here.some((p) => p.me);
        const edge = n === 1 || n === cells;
        return (
          <g key={n}>
            <circle
              cx={x}
              cy={y}
              r={edge ? 20 : 14}
              className={mine ? 'c-dot c-dot-me' : edge ? 'c-dot c-dot-edge' : 'c-dot'}
            />
            <text x={x} y={y + 4} className={edge ? 'c-num c-num-edge' : 'c-num'}>
              {edge ? '' : n}
            </text>
            {edge ? (
              <text x={x} y={y + 3} className="c-edge-label">
                {n === 1 ? t.start : t.finish}
              </text>
            ) : null}
            {here.map((p, k) => (
              <g key={p.name}>
                <circle cx={x + 12 + k * 6} cy={y - 14} r={9} fill={p.color} className="c-mini" />
                <text x={x + 12 + k * 6} y={y - 11} className="c-mini-t">
                  {p.name[0]}
                </text>
              </g>
            ))}
          </g>
        );
      })}
    </svg>
  );
}

function Ranking({ limit }: { limit?: number }) {
  return (
    <ol className="c-rank">
      {(limit ? players.slice(0, limit) : players).map((p, i) => (
        <li key={p.name} className={p.me ? 'c-me' : undefined}>
          <span className="c-pos">{i + 1}</span>
          <Sticker player={p} size={32} />
          <span className="c-name">
            {p.name}
            {p.first ? <span className="c-chip c-chip-gold">{t.first}</span> : null}
            {p.me ? <span className="c-chip">{t.you}</span> : null}
            {p.inactive ? <span className="c-soft"> {t.inactive}</span> : null}
            <span className="c-soft c-block">{t.toFinish(cells - p.cell)}</span>
          </span>
          <span className="c-pts">{p.points}</span>
        </li>
      ))}
    </ol>
  );
}

export function PreviewC() {
  const lisa = players.find((p) => p.me);
  return (
    <div className="c-root">
      <header className="c-top">
        <strong className="c-title">{season.name}</strong>
        <span className="c-soft">{t.deadline(season.deadline)}</span>
      </header>

      <main className="c-layout">
        <section className="c-now" aria-label={t.nowPlaying}>
          <div className="c-now-head">
            {lisa ? <Sticker player={lisa} size={48} /> : null}
            <div>
              <p className="c-soft">{t.nowPlaying}</p>
              <h1 className="c-game">{me.game}</h1>
            </div>
          </div>
          <p className="c-tags">
            {me.tags.map((tag) => (
              <span key={tag}>{tag}</span>
            ))}
          </p>
          <p className="c-soft">
            {t.hours(me.hours)}, {t.started(me.startedAgo).toLowerCase()}
          </p>
          <div className="c-actions">
            <button type="button" className="c-go">
              {t.complete}
            </button>
            <button type="button" className="c-quiet">
              {t.drop}
            </button>
          </div>
        </section>

        <section className="c-map" aria-label={t.map}>
          <Route columns={6} className="c-phone" />
          <Route columns={10} className="c-desk" />
        </section>

        <button type="button" className="c-sheet c-phone" aria-label={t.leaderboard}>
          <span className="c-handle" />
          <strong>{t.leaderboard}</strong>
          <span>{t.sheet(3, lisa?.points ?? 0)}</span>
        </button>

        <aside className="c-side">
          <section className="c-card" aria-label={t.leaderboard}>
            <h2 className="c-h">{t.leaderboard}</h2>
            <div className="c-phone">
              <Ranking limit={3} />
            </div>
            <div className="c-desk">
              <Ranking />
            </div>
          </section>
          <section className="c-card c-desk" aria-label={t.feed}>
            <h2 className="c-h">{t.feed}</h2>
            <ul className="c-chat">
              {feed.map((e) => {
                const who = players.find((p) => p.name === e.who);
                return (
                  <li key={e.what}>
                    {who ? <Sticker player={who} size={28} /> : null}
                    <p className="c-bubble">
                      <strong>{e.who}</strong> {e.what}
                      {e.dice ? <span className="c-roll">{e.dice.join(' + ')}</span> : null}
                      <time>{e.when}</time>
                    </p>
                  </li>
                );
              })}
            </ul>
          </section>
        </aside>
      </main>
    </div>
  );
}
