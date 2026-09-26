import '@fontsource/handjet/cyrillic-700.css';
import '@fontsource/handjet/latin-700.css';
import '@fontsource/onest/cyrillic-400.css';
import '@fontsource/onest/cyrillic-600.css';
import '@fontsource/onest/latin-400.css';
import '@fontsource/onest/latin-600.css';
import './preview-b.css';
import { ru } from '../i18n/ru';
import { boardCells, playersOn } from './Board';
import { cells, feed, me, players, season } from './data';

const t = ru.designPreview;

// B — «Сейв-файл»: the site as a game's HUD in daylight. Numbers and headings in a pixel face (Handjet), the rest in Onest;
// progress shown as bars you fill, the current run as a quest card, the map as a level of square tiles.

function Bar({ value, max, tone }: { value: number; max: number; tone: 'xp' | 'hp' }) {
  return (
    <span className={`b-bar b-${tone}`} role="img" aria-label={`${value} / ${max}`}>
      <span style={{ width: `${Math.round((value / max) * 100)}%` }} />
    </span>
  );
}

function Level({ columns, className }: { columns: number; className: string }) {
  return (
    <ol
      className={`b-level ${className}`}
      style={{ gridTemplateColumns: `repeat(${columns}, 1fr)` }}
    >
      {boardCells(columns).map(({ n }) => {
        const here = playersOn(n);
        return (
          <li
            key={n}
            className={`b-tile ${n === 1 ? 'b-start' : ''} ${n === cells ? 'b-finish' : ''} ${here.some((p) => p.me) ? 'b-mine' : ''} ${n % 10 === 0 && n !== cells ? 'b-save' : ''}`}
          >
            <span className="b-n">{n === 1 ? t.start : n === cells ? t.finish : n}</span>
            <span className="b-sprites">
              {here.map((p) => (
                <span
                  key={p.name}
                  className="b-sprite"
                  style={{ background: p.color }}
                  title={p.name}
                >
                  {p.name[0]}
                </span>
              ))}
            </span>
          </li>
        );
      })}
    </ol>
  );
}

function Party({ limit }: { limit?: number }) {
  const top = players[0]?.points ?? 1;
  return (
    <ol className="b-party">
      {(limit ? players.slice(0, limit) : players).map((p, i) => (
        <li key={p.name} className={p.me ? 'b-me' : undefined}>
          <span className="b-rank">{i + 1}</span>
          <span className="b-sprite b-sprite-lg" style={{ background: p.color }}>
            {p.name[0]}
          </span>
          <span className="b-who">
            <span>
              {p.name}
              {p.first ? <span className="b-tag">{t.first}</span> : null}
              {p.me ? <span className="b-tag b-tag-me">{t.you}</span> : null}
              {p.inactive ? <span className="b-off"> {t.inactive}</span> : null}
            </span>
            <Bar value={p.points} max={top} tone="xp" />
          </span>
          <span className="b-score">{p.points}</span>
        </li>
      ))}
    </ol>
  );
}

export function PreviewB() {
  const mine = players.find((p) => p.me);
  return (
    <div className="b-root">
      <header className="b-top">
        <strong className="b-title">{season.name}</strong>
        <span className="b-clock">{t.daysLeft(season.daysLeft)}</span>
      </header>

      <main className="b-layout">
        <section className="b-quest" aria-label={t.nowPlaying}>
          <p className="b-kicker">{t.quest}</p>
          <h1 className="b-game">{me.game}</h1>
          <p className="b-sub">{me.tags.join(', ')}</p>
          <dl className="b-stats">
            <div>
              <dt>{t.level}</dt>
              <dd>
                {mine?.cell}/{cells}
              </dd>
            </div>
            <div>
              <dt>{t.leaderboard}</dt>
              <dd>{t.place(3)}</dd>
            </div>
            <div>
              <dt>{t.hltb}</dt>
              <dd>{t.hoursShort(me.hours)}</dd>
            </div>
          </dl>
          <Bar value={mine?.cell ?? 0} max={cells} tone="hp" />
          <p className="b-sub">{t.toFinish(cells - (mine?.cell ?? 0))}</p>
          <div className="b-actions">
            <button type="button" className="b-primary">
              {t.complete}
            </button>
            <button type="button" className="b-ghost">
              {t.drop}
            </button>
          </div>
        </section>

        <section className="b-map" aria-label={t.map}>
          <h2 className="b-h">{t.map}</h2>
          <Level columns={6} className="b-phone" />
          <Level columns={10} className="b-desk" />
        </section>

        <button type="button" className="b-sheet b-phone" aria-label={t.leaderboard}>
          <span className="b-handle" />
          <strong>{t.leaderboard}</strong>
          <span>{t.sheet(3, mine?.points ?? 0)}</span>
        </button>

        <aside className="b-side">
          <section className="b-panel" aria-label={t.leaderboard}>
            <h2 className="b-h">{t.leaderboard}</h2>
            <div className="b-phone">
              <Party limit={3} />
            </div>
            <div className="b-desk">
              <Party />
            </div>
          </section>
          <section className="b-panel b-desk" aria-label={t.feed}>
            <h2 className="b-h">{t.feed}</h2>
            <ul className="b-log">
              {feed.map((e) => (
                <li key={e.what}>
                  <time>{e.when}</time>
                  <span>
                    <strong>{e.who}</strong> {e.what}
                    {e.dice ? <span className="b-dice">{e.dice.join(' + ')}</span> : null}
                  </span>
                </li>
              ))}
            </ul>
          </section>
        </aside>
      </main>
    </div>
  );
}
