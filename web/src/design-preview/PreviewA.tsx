import '@fontsource/rubik/cyrillic-500.css';
import '@fontsource/rubik/cyrillic-700.css';
import '@fontsource/rubik/cyrillic-800.css';
import '@fontsource/rubik/latin-500.css';
import '@fontsource/rubik/latin-700.css';
import './preview-a.css';
import { ru } from '../i18n/ru';
import { boardCells, playersOn } from './Board';
import { cells, feed, me, players, season } from './data';

const t = ru.designPreview;

// A — «Игровое поле»: the site as a board game box. Cardboard tiles with a thick ink outline and a solid offset shadow,
// real dice with pips, the one yellow button. Rubik: rounded, friendly, full Cyrillic.

function Pips({ value }: { value: number }) {
  const spots: Record<number, number[]> = {
    1: [4],
    2: [0, 8],
    3: [0, 4, 8],
    4: [0, 2, 6, 8],
    5: [0, 2, 4, 6, 8],
    6: [0, 2, 3, 5, 6, 8],
  };
  return (
    <span className="a-die" aria-label={String(value)}>
      {Array.from({ length: 9 }, (_, i) => (
        <span key={i} className={spots[value]?.includes(i) ? 'a-pip' : undefined} />
      ))}
    </span>
  );
}

function BoardGrid({ columns, className }: { columns: number; className: string }) {
  return (
    <ol
      className={`a-board ${className}`}
      style={{ gridTemplateColumns: `repeat(${columns}, 1fr)` }}
    >
      {boardCells(columns).map(({ n }) => {
        const here = playersOn(n);
        const mine = here.some((p) => p.me);
        return (
          <li
            key={n}
            className={`a-cell ${n === 1 ? 'a-start' : ''} ${n === cells ? 'a-finish' : ''} ${mine ? 'a-mine' : ''} ${n % 10 === 0 && n !== cells ? 'a-check' : ''}`}
          >
            <span className="a-n">{n === 1 ? t.start : n === cells ? t.finish : n}</span>
            <span className="a-tokens">
              {here.map((p) => (
                <span
                  key={p.name}
                  className="a-token"
                  style={{ background: p.color }}
                  title={p.name}
                />
              ))}
            </span>
          </li>
        );
      })}
    </ol>
  );
}

function Leaders({ limit }: { limit?: number }) {
  const rows = limit ? players.slice(0, limit) : players;
  return (
    <ol className="a-leaders">
      {rows.map((p, i) => (
        <li key={p.name} className={p.me ? 'a-me' : undefined}>
          <span className="a-place">{i + 1}</span>
          <span className="a-token a-token-lg" style={{ background: p.color }} />
          <span className="a-name">
            {p.name}
            {p.me ? <strong className="a-badge a-badge-me">{t.you}</strong> : null}
            {p.first ? <strong className="a-badge">{t.first}</strong> : null}
            {p.inactive ? <em className="a-muted"> {t.inactive}</em> : null}
          </span>
          <span className="a-points">{p.points}</span>
          <span className="a-left">{t.toFinish(cells - p.cell)}</span>
        </li>
      ))}
    </ol>
  );
}

export function PreviewA() {
  return (
    <div className="a-root">
      <header className="a-top">
        <strong className="a-season">{season.name}</strong>
        <span>{t.deadline(season.deadline)}</span>
      </header>

      <main className="a-layout">
        <section className="a-turn" aria-label={t.nowPlaying}>
          <p className="a-label">{t.nowPlaying}</p>
          <h1 className="a-game">{me.game}</h1>
          <p className="a-meta">{me.tags.join(', ')}</p>
          <p className="a-meta">{t.hours(me.hours)}</p>
          <p className="a-meta">{t.started(me.startedAgo)}</p>
          <div className="a-actions">
            <button className="a-primary" type="button">
              {t.complete}
            </button>
            <button className="a-secondary" type="button">
              {t.drop}
            </button>
          </div>
          <div className="a-last" aria-label={t.lastRoll}>
            <Pips value={4} />
            <Pips value={6} />
            <span>{t.lastRollOf('Сова', 10)}</span>
          </div>
        </section>

        <section className="a-map" aria-label={t.map}>
          <BoardGrid columns={6} className="a-phone" />
          <BoardGrid columns={10} className="a-desk" />
        </section>

        <button type="button" className="a-sheet a-phone" aria-label={t.leaderboard}>
          <span className="a-handle" />
          <strong>{t.leaderboard}</strong>
          <span>{t.sheet(3, 52)}</span>
        </button>

        <aside className="a-side">
          <section className="a-panel" aria-label={t.leaderboard}>
            <h2>{t.leaderboard}</h2>
            <div className="a-phone">
              <Leaders limit={3} />
              <button className="a-link" type="button">
                {t.showAll}
              </button>
            </div>
            <div className="a-desk">
              <Leaders />
            </div>
          </section>
          <section className="a-panel a-desk" aria-label={t.feed}>
            <h2>{t.feed}</h2>
            <ul className="a-feed">
              {feed.map((e) => (
                <li key={e.what}>
                  <strong>{e.who}</strong> {e.what}
                  {e.dice ? (
                    <span className="a-feed-dice">
                      {e.dice.map((d, i) => (
                        <Pips key={i} value={d} />
                      ))}
                    </span>
                  ) : null}
                  <time>{e.when}</time>
                </li>
              ))}
            </ul>
          </section>
        </aside>
      </main>
    </div>
  );
}
