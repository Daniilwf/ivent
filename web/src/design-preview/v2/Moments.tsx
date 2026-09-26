import { animate, motion, useMotionValue, useReducedMotion } from 'motion/react';
import { useEffect, useRef, useState } from 'react';
import { ru } from '../../i18n/ru';
import type { Content, Player } from './content';
import { cell, graph, walk, type Point } from './graph';
import { MapSticker, MapView } from './MapView';
import { Sticker } from './MainScreen';

const t = ru.designPreview;

// The main moments (DESIGN.md «Движение»): the only places with a show. Each lasts under three seconds, a tap on the
// stage or the main button skips to the result, reduced motion shows the result at once, and the result is also said
// in words (aria-live). While a moment plays, the main button turns into «show the result», so focus never drops.

function Head({ title, lead }: { title: string; lead: string }) {
  return (
    <header className="proto-head">
      <a className="back" href="#">
        {t.back}
      </a>
      <h1>{title}</h1>
      <p>{lead}</p>
    </header>
  );
}

// ---------------------------------------------------------------- The wheel

const categories = [
  { name: 'Action', games: 3 },
  { name: 'Horror', games: 4 },
  { name: 'Platformer', games: 2 },
  { name: 'RPG', games: 3 },
  { name: 'Racing', games: 0 },
  { name: 'Indie', games: 5 },
  { name: 'Puzzle', games: 2 },
  { name: 'Visual Novel', games: 0 },
  { name: 'Rogue-like', games: 2 },
  { name: 'Adventure', games: 4 },
  { name: 'Shooter', games: 1 },
  { name: 'Cozy', games: 1 },
];

const sectorColors = [
  'var(--zone-meadow)',
  'var(--zone-city)',
  'var(--zone-swamp)',
  'var(--gold)',
  'var(--zone-castle)',
  'var(--zone-mountains)',
];

export function WheelMoment({ content }: { content: Content }) {
  const reduce = useReducedMotion();
  const live = categories.filter((c) => c.games > 0);
  const rotation = useMotionValue(0);
  const running = useRef<ReturnType<typeof animate> | null>(null);
  const [phase, setPhase] = useState<'idle' | 'spinning' | 'miss' | 'again' | 'done'>('idle');
  const [pick, setPick] = useState(0);
  const sector = 360 / live.length;
  const miss = content.games[1];
  const game = content.games[10];
  const taker = content.players[1] as Player;
  const timer = useRef<number | undefined>(undefined);
  const playing = phase === 'spinning' || phase === 'miss' || phase === 'again';

  const finish = () => {
    running.current?.stop();
    window.clearTimeout(timer.current);
    rotation.set(-(pick * sector + sector / 2) - 360 * 6);
    setPhase('done');
  };

  // The server's answer arrives whole: a miss (a game someone already completed this season) and then the pick.
  // The wheel shows both: it lands on the miss, says who completed it, and spins on to the pick.
  const spin = () => {
    const missAt = (pick + 2 + Math.floor(Math.random() * (live.length - 1))) % live.length;
    const next = (missAt + 2 + Math.floor(Math.random() * (live.length - 3))) % live.length;
    setPick(next);
    const turn = (at: number, laps: number) => -(at * sector + sector / 2) - 360 * laps;
    if (reduce) {
      rotation.set(turn(next, 6));
      setPhase('done');
      return;
    }
    rotation.set(rotation.get() % 360);
    setPhase('spinning');
    running.current = animate(rotation, turn(missAt, 4), {
      duration: 1.4,
      ease: [0.12, 0.8, 0.2, 1],
    });
    void running.current.then(() => {
      setPhase((p) => (p === 'spinning' ? 'miss' : p));
      timer.current = window.setTimeout(() => {
        setPhase((p) => (p === 'miss' ? 'again' : p));
        running.current = animate(rotation, turn(next, 6), {
          duration: 0.7,
          ease: [0.2, 0.7, 0.3, 1],
        });
        void running.current.then(() => {
          setPhase((p) => (p === 'again' ? 'done' : p));
        });
      }, 1000);
    });
  };

  useEffect(
    () => () => {
      window.clearTimeout(timer.current);
    },
    [],
  );

  const category = live[pick]?.name ?? '';
  const size = 300;
  const r = size / 2;
  return (
    <div className="proto">
      <Head title={t.wheel.title} lead={t.wheel.lead} />
      <div
        className="stage stage-wheel"
        onClick={() => {
          if (playing) finish();
        }}
      >
        <div style={{ position: 'relative', width: size, maxWidth: '100%' }}>
          <svg viewBox={`-10 -22 ${size + 20} ${size + 32}`} width="100%" aria-hidden>
            <motion.g style={{ rotate: rotation, originX: `${r}px`, originY: `${r}px` }}>
              {live.map((c, i) => {
                const a0 = ((i * sector - 90) * Math.PI) / 180;
                const a1 = (((i + 1) * sector - 90) * Math.PI) / 180;
                const midDeg = (i + 0.5) * sector - 90;
                const mid = (midDeg * Math.PI) / 180;
                const lx = r + r * 0.6 * Math.cos(mid);
                const ly = r + r * 0.6 * Math.sin(mid);
                // Labels run along the radius; on the left half they turn over to stay upright
                const turn = Math.cos(mid) < 0 ? midDeg + 180 : midDeg;
                return (
                  <g key={c.name}>
                    <path
                      d={`M${r} ${r} L${r + r * Math.cos(a0)} ${r + r * Math.sin(a0)} A${r} ${r} 0 0 1 ${r + r * Math.cos(a1)} ${r + r * Math.sin(a1)} Z`}
                      fill={sectorColors[i % sectorColors.length]}
                      stroke="var(--ink)"
                      strokeWidth={3}
                    />
                    <text
                      x={lx}
                      y={ly}
                      transform={`rotate(${turn} ${lx} ${ly})`}
                      className="cell-num"
                      style={{ fontSize: 12 }}
                    >
                      {c.name}
                    </text>
                  </g>
                );
              })}
            </motion.g>
            <circle cx={r} cy={r} r={26} fill="var(--card)" stroke="var(--ink)" strokeWidth={4} />
            <circle cx={r} cy={r} r={r} fill="none" stroke="var(--ink)" strokeWidth={6} />
            <path
              d={`M${r - 16} -18 L${r + 16} -18 L${r} 16 Z`}
              fill="var(--action)"
              stroke="var(--ink)"
              strokeWidth={4}
              strokeLinejoin="round"
            />
          </svg>
        </div>
        {phase === 'miss' && miss ? (
          <motion.div
            className="reveal"
            initial={{ scale: 0.6, opacity: 0 }}
            animate={{ scale: 1, opacity: 1 }}
            transition={{ duration: 0.18 }}
          >
            {miss.cover ? (
              <img src={miss.cover} alt="" width={90} height={135} className="missed" />
            ) : null}
            <span className="clamp-2 missed-title">{miss.title}</span>
            <strong>{t.wheel.miss(taker.name)}</strong>
          </motion.div>
        ) : null}
        {phase === 'done' && game ? (
          <motion.div
            className="reveal"
            initial={reduce ? false : { rotateY: 90, opacity: 0 }}
            animate={{ rotateY: 0, opacity: 1 }}
            transition={{ duration: 0.25 }}
          >
            {game.cover ? <img src={game.cover} alt="" width={110} height={165} /> : null}
            <strong>{game.title}</strong>
            <span className="hint">{t.wheel.category(category)}</span>
            {miss ? (
              <span className="miss-note">
                {t.wheel.missNote(miss.title)} {t.wheel.miss(taker.name)}
              </span>
            ) : null}
          </motion.div>
        ) : null}
      </div>
      <p className="result" aria-live="polite">
        {phase === 'done' && game
          ? `${miss ? `${t.wheel.missNote(miss.title)} ${t.wheel.miss(taker.name)}. ` : ''}${t.wheel.category(category)}. ${t.wheel.result(game.title)}`
          : ' '}
      </p>
      <div className="row">
        {phase === 'done' ? (
          <>
            <button type="button" className="btn-main">
              {t.wheel.start}
            </button>
            <button type="button" className="btn-quiet btn-plain" onClick={spin}>
              {t.wheel.again}
            </button>
          </>
        ) : (
          <button type="button" className="btn-main" onClick={playing ? finish : spin}>
            {playing ? t.skip : t.wheel.spin}
          </button>
        )}
      </div>
      <p className="hint">
        {t.skipHint}.
        <br />
        {t.wheel.empty}:{' '}
        {categories
          .filter((c) => c.games === 0)
          .map((c) => c.name)
          .join(', ')}
      </p>
    </div>
  );
}

// ---------------------------------------------------------------- The dice

const pips: Record<number, number[]> = {
  1: [4],
  2: [0, 8],
  3: [0, 4, 8],
  4: [0, 2, 6, 8],
  5: [0, 2, 4, 6, 8],
  6: [0, 2, 3, 5, 6, 8],
};
// The cube turns to show a face at the front: 1 front, 6 back, 3 right, 4 left, 2 top, 5 bottom
const faceTurn: Record<number, [number, number]> = {
  1: [0, 0],
  6: [0, 180],
  3: [0, -90],
  4: [0, 90],
  2: [-90, 0],
  5: [90, 0],
};

function Face({ value, className }: { value: number; className: string }) {
  return (
    <span className={`face ${className}`}>
      {Array.from({ length: 9 }, (_, i) => (
        <span key={i} className={pips[value]?.includes(i) ? 'pip' : undefined} />
      ))}
    </span>
  );
}

/** A die thrown in from the left edge of the table: it tumbles, bounces and settles on its face */
function Die({
  value,
  index,
  thrown,
  challenge,
}: {
  value: number;
  index: number;
  thrown: boolean;
  challenge?: boolean;
}) {
  const [x, y] = faceTurn[value] ?? [0, 0];
  const delay = index * 0.09;
  return (
    <span className={`die ${challenge ? 'die-challenge' : ''}`}>
      <motion.span
        style={{ display: 'block' }}
        initial={thrown ? { x: -260 - index * 30, y: -90, rotate: -200 } : false}
        animate={{ x: 0, y: 0, rotate: 0 }}
        transition={
          thrown
            ? { type: 'spring', stiffness: 110, damping: 11, mass: 0.9, delay }
            : { duration: 0 }
        }
      >
        <motion.span
          className="cube"
          initial={thrown ? { rotateX: 0, rotateY: 0 } : false}
          animate={{ rotateX: x + 720, rotateY: y + 720 }}
          transition={thrown ? { duration: 1.2, delay, ease: [0.2, 0.7, 0.3, 1] } : { duration: 0 }}
        >
          <Face value={1} className="f1" />
          <Face value={6} className="f6" />
          <Face value={3} className="f3" />
          <Face value={4} className="f4" />
          <Face value={2} className="f2" />
          <Face value={5} className="f5" />
        </motion.span>
      </motion.span>
      {challenge ? <span className="die-label">{t.dice.challengeDie}</span> : null}
    </span>
  );
}

export function DiceMoment() {
  const reduce = useReducedMotion() ?? false;
  const [roll, setRoll] = useState(0);
  const [skipped, setSkipped] = useState(false);
  const [values, setValues] = useState([6, 6, 6, 6]);
  const [shown, setShown] = useState<number | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const total = values.reduce((a, b) => a + b, 0);
  const playing = roll > 0 && shown === null;
  const thrown = roll > 0 && !skipped && !reduce;

  const throwDice = () => {
    const next = values.map(() => 1 + Math.floor(Math.random() * 6));
    setValues(next);
    setRoll((r) => r + 1);
    setSkipped(false);
    setShown(null);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(
      () => {
        setShown(next.reduce((a, b) => a + b, 0));
      },
      reduce ? 0 : 1500,
    );
  };

  const skip = () => {
    if (!playing) return;
    window.clearTimeout(timer.current);
    setSkipped(true);
    setShown(total);
  };

  useEffect(
    () => () => {
      window.clearTimeout(timer.current);
    },
    [],
  );

  return (
    <div className="proto">
      <Head title={t.dice.title} lead={t.dice.lead} />
      <div className="stage" onClick={skip}>
        <div className="dice-row">
          {values.map((v, i) => (
            <Die
              key={`${roll}-${skipped ? 's' : 'r'}-${i}`}
              value={v}
              index={i}
              thrown={thrown}
              challenge={i === 3}
            />
          ))}
        </div>
        {shown !== null ? (
          <motion.div
            className="total"
            initial={reduce ? false : { scale: 0.5, opacity: 0 }}
            animate={{ scale: 1, opacity: 1 }}
            transition={{ type: 'spring', stiffness: 400, damping: 18 }}
          >
            +{shown}
          </motion.div>
        ) : null}
      </div>
      <p className="result" aria-live="polite">
        {shown !== null
          ? `${values.slice(0, 3).join(' + ')} + ${values[3] ?? 0} ${t.dice.challenge}. ${t.dice.total(shown)}`
          : ' '}
      </p>
      <button type="button" className="btn-main" onClick={playing ? skip : throwDice}>
        {playing ? t.skip : roll === 0 ? t.dice.roll : t.dice.again}
      </button>
      <p className="hint">{t.skipHint}</p>
    </div>
  );
}

// ---------------------------------------------------------------- Hops along the route

function useHops(
  path: number[],
  reduce: boolean,
  onStep: (p: Point) => void,
  onHop: (to: Point) => void,
  onDone: () => void,
) {
  const controls = useRef<ReturnType<typeof animate> | null>(null);
  const stopped = useRef(false);
  const run = async () => {
    stopped.current = false;
    const isStopped = () => stopped.current;
    for (let i = 1; i < path.length && !isStopped(); i++) {
      const a = cell(path[i - 1] as number);
      const b = cell(path[i] as number);
      if (reduce) continue;
      // The camera moves once a hop, not every frame
      onHop(b);
      controls.current = animate(0, 1, {
        duration: 0.22,
        ease: 'easeInOut',
        onUpdate: (k) => {
          onStep({
            x: a.x + (b.x - a.x) * k,
            y: a.y + (b.y - a.y) * k - Math.sin(Math.PI * k) * 22,
          });
        },
      });
      await controls.current;
    }
    const last = cell(path[path.length - 1] as number);
    onStep(last);
    onHop(last);
    onDone();
  };
  const skip = () => {
    stopped.current = true;
    controls.current?.stop();
  };
  return { run, skip };
}

// ---------------------------------------------------------------- The move

export function MoveMoment({ content }: { content: Content }) {
  const reduce = useReducedMotion() ?? false;
  const from = 9;
  // Steps that pass the first fork and stop on an event cell
  const steps =
    [7, 8, 6, 9, 5].find((n) => cell(walk(from, n).at(-1) ?? from).kind === 'event') ?? 7;
  const path = walk(from, steps);
  const me = { ...(content.players.find((p) => p.me) as Player), cell: from };
  const [pos, setPos] = useState<Point>(cell(from));
  const [phase, setPhase] = useState<'idle' | 'moving' | 'done'>('idle');
  const camera = useRef<(p: Point, w?: number) => void>(() => undefined);
  const hops = useHops(
    path,
    reduce,
    setPos,
    (p) => {
      camera.current(p, 420);
    },
    () => {
      setPhase('done');
    },
  );
  const forkPassed = phase !== 'idle' && path.some((id) => cell(id).kind === 'fork');
  const players = content.players.map((p) => (p.me ? me : p));

  return (
    <div className="proto">
      <Head title={t.move.title} lead={t.move.lead} />
      <div
        className="stage stage-map"
        onClick={() => {
          if (phase === 'moving') hops.skip();
        }}
      >
        <MapView
          variant="world"
          players={players}
          hide={[me.id]}
          focus={from}
          className="proto-map"
          controls={(api) => {
            camera.current = api.centerOn;
          }}
        >
          <MapSticker player={me} at={pos} />
        </MapView>
        {forkPassed ? <span className="toast">{t.move.fork}</span> : null}
        {phase === 'done' ? (
          <motion.div
            className="reveal event-card"
            initial={reduce ? false : { y: 24, opacity: 0 }}
            animate={{ y: 0, opacity: 1 }}
            transition={{ duration: 0.22 }}
          >
            <strong>{t.move.landed(path.at(-1) ?? from)}</strong>
          </motion.div>
        ) : null}
      </div>
      <p className="result" aria-live="polite">
        {phase === 'done' ? t.move.landed(path.at(-1) ?? from) : ' '}
      </p>
      <div className="row">
        <button
          type="button"
          className="btn-main"
          onClick={() => {
            if (phase === 'moving') {
              hops.skip();
              return;
            }
            if (phase === 'done') {
              setPos(cell(from));
              camera.current(cell(from), 420);
            }
            setPhase('moving');
            void hops.run();
          }}
        >
          {phase === 'moving' ? t.skip : t.move.go(steps)}
        </button>
        {phase === 'done' ? (
          <button
            type="button"
            className="btn-quiet btn-plain"
            onClick={() => {
              setPos(cell(from));
              camera.current(cell(from), 420);
              setPhase('idle');
            }}
          >
            {t.move.reset}
          </button>
        ) : null}
      </div>
      <p className="hint">{t.skipHint}</p>
    </div>
  );
}

// ---------------------------------------------------------------- The finish

function Confetti({ run }: { run: number }) {
  const canvas = useRef<HTMLCanvasElement | null>(null);
  useEffect(() => {
    const c = canvas.current;
    const g = c?.getContext('2d');
    if (!c || !g || run === 0) return;
    const colors = ['#E69F00', '#56B4E9', '#009E73', '#F0E442', '#D55E00', '#CC79A7'];
    c.width = c.clientWidth * devicePixelRatio;
    c.height = c.clientHeight * devicePixelRatio;
    const bits = Array.from({ length: 140 }, () => ({
      x: c.width / 2,
      y: c.height * 0.4,
      vx: (Math.random() - 0.5) * 16 * devicePixelRatio,
      vy: (-Math.random() * 14 - 4) * devicePixelRatio,
      r: (4 + Math.random() * 5) * devicePixelRatio,
      spin: Math.random() * 6,
      color: colors[Math.floor(Math.random() * colors.length)] as string,
    }));
    let frame = 0;
    let id = 0;
    const tick = () => {
      g.clearRect(0, 0, c.width, c.height);
      for (const b of bits) {
        b.vy += 0.45 * devicePixelRatio;
        b.x += b.vx;
        b.y += b.vy;
        g.save();
        g.translate(b.x, b.y);
        g.rotate(b.spin + frame / 10);
        g.fillStyle = b.color;
        g.fillRect(-b.r, -b.r / 2, b.r * 2, b.r);
        g.restore();
      }
      if (++frame < 110) id = requestAnimationFrame(tick);
      else g.clearRect(0, 0, c.width, c.height);
    };
    id = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(id);
    };
  }, [run]);
  return <canvas ref={canvas} className="confetti" aria-hidden />;
}

const finishId = (graph.cells.find((c) => c.kind === 'finish') ?? graph.cells.at(-1))?.id ?? 1;
// The cell four hops before the finish along the route
const finishFrom =
  graph.cells.find((c) => walk(c.id, 4).at(-1) === finishId && walk(c.id, 4).length === 5)?.id ??
  finishId;

export function FinishMoment({ content }: { content: Content }) {
  const reduce = useReducedMotion() ?? false;
  const winner = { ...(content.players[0] as Player), cell: finishFrom };
  const path = walk(finishFrom, 4);
  const [pos, setPos] = useState<Point>(cell(finishFrom));
  const [phase, setPhase] = useState<'idle' | 'moving' | 'done'>('idle');
  const [party, setParty] = useState(0);
  const camera = useRef<(p: Point, w?: number) => void>(() => undefined);
  const hops = useHops(
    path,
    reduce,
    setPos,
    () => undefined,
    () => {
      setPhase('done');
      if (!reduce) setParty((p) => p + 1);
    },
  );
  const players = content.players.map((p, i) => (i === 0 ? winner : p));

  const go = () => {
    if (phase === 'moving') {
      hops.skip();
      return;
    }
    setPos(cell(finishFrom));
    setPhase('moving');
    void hops.run();
  };

  return (
    <div className="proto">
      <Head title={t.finish.title} lead={t.finish.lead} />
      <div
        className="stage stage-map"
        onClick={() => {
          if (phase === 'moving') hops.skip();
        }}
      >
        <MapView
          variant="world"
          players={players}
          hide={[winner.id]}
          focus={path[2]}
          className="proto-map"
          controls={(api) => {
            camera.current = api.centerOn;
          }}
        >
          <MapSticker player={winner} at={pos} size={40} />
        </MapView>
        <Confetti run={party} />
        {phase === 'done' ? (
          <motion.div
            className="reveal finish-card"
            initial={reduce ? false : { scale: 0.6, opacity: 0 }}
            animate={{ scale: 1, opacity: 1 }}
            transition={{ type: 'spring', stiffness: 380, damping: 16 }}
          >
            <Sticker player={winner} size={72} />
            <strong>{t.finish.first(winner.name)}</strong>
            <span className="chip">{t.finish.provisional}</span>
            <span className="hint">{t.finish.frozen}</span>
          </motion.div>
        ) : null}
      </div>
      <p className="result" aria-live="polite">
        {phase === 'done' ? `${t.finish.first(winner.name)} ${t.finish.provisional}` : ' '}
      </p>
      <button type="button" className="btn-main" onClick={go}>
        {phase === 'moving' ? t.skip : phase === 'done' ? t.finish.again : t.finish.go}
      </button>
      <p className="hint">{t.skipHint}</p>
    </div>
  );
}
