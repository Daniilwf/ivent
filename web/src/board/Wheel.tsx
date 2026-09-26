import { animate, motion, useMotionValue, useReducedMotion } from 'motion/react';
import { useEffect, useEffectEvent, useImperativeHandle, useRef, useState, type Ref } from 'react';
import { ru } from '../i18n/ru';
import { MomentCard, Table } from '../ui/Surface';
import { momentBudget, type MomentHandle, type MomentPhase } from './moment';

const t = ru.moments.wheel;

export type WheelGame = { title: string; cover?: string | undefined };

/** The server's answer, whole: the misses on the way (games someone completed or plays now) and the pick */
export type WheelRoll = {
  id: number;
  misses: { sector: number; game: WheelGame; by: string; playing?: boolean | undefined }[];
  /** The game, or none when the roll offers a choice of `choices` games of the category */
  pick: { sector: number; game: WheelGame | null; choices?: number | undefined };
};

const sectorFills = [
  'var(--color-zone-meadow)',
  'var(--color-zone-city)',
  'var(--color-zone-swamp)',
  'var(--color-gold)',
  'var(--color-zone-castle)',
  'var(--color-zone-mountains)',
];

type Stage = 'idle' | 'spinning' | 'miss' | 'done';

/** The category wheel: only categories with free games are on it; it stops on each miss, says who completed that
 *  game, and spins on to the pick. Give it `key={roll.id}`: every roll is a fresh moment. */
export function WheelMoment({
  sectors,
  roll,
  onPhase,
  ref,
}: {
  sectors: string[];
  roll: WheelRoll | null;
  onPhase?: ((phase: MomentPhase) => void) | undefined;
  ref?: Ref<MomentHandle> | undefined;
}) {
  const reduce = useReducedMotion() ?? false;
  const sector = 360 / Math.max(sectors.length, 1);
  const turn = (at: number, laps: number) => -(at * sector + sector / 2) - 360 * laps;
  const [stage, setStage] = useState<Stage>(roll ? (reduce ? 'done' : 'spinning') : 'idle');
  const [missShown, setMissShown] = useState(0);
  const rotation = useMotionValue(roll && reduce ? turn(roll.pick.sector, 6) : 0);
  const running = useRef<ReturnType<typeof animate> | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const cancelled = useRef(false);
  // The end is reported once, whether the wheel stops or the moment is skipped
  const ended = useRef(false);

  const land = useEffectEvent(() => {
    if (ended.current) return;
    ended.current = true;
    setStage('done');
    onPhase?.('done');
  });
  const showMiss = useEffectEvent((i: number) => {
    setMissShown(i);
    setStage('miss');
  });
  const spinOn = useEffectEvent(() => {
    setStage('spinning');
  });

  const finish = () => {
    if (!roll || ended.current) return;
    ended.current = true;
    cancelled.current = true;
    running.current?.stop();
    window.clearTimeout(timer.current);
    rotation.set(turn(roll.pick.sector, 6));
    setStage('done');
    onPhase?.('done');
  };
  useImperativeHandle(ref, () => ({ skip: finish }));

  // One roll per mount (the parent keys the wheel by it): a page refresh hands in an equal roll as a new object,
  // which must not restart the spin
  const rollId = roll?.id;
  useEffect(() => {
    if (!roll) return;
    if (reduce) {
      // Reduced motion: the wheel already stands on the pick; the end is reported at once
      const id = window.setTimeout(land, 0);
      return () => {
        window.clearTimeout(id);
      };
    }
    cancelled.current = false;
    const stops = [...roll.misses.map((m) => m.sector), roll.pick.sector];
    // The whole roll fits the moments' budget: the first spin, then for each miss a rest and a short spin
    const firstSpin = 1.2;
    const perMiss = Math.min(1.2, (momentBudget - firstSpin) / Math.max(roll.misses.length, 1));
    const missRest = perMiss * 0.55;
    const spinOnFor = perMiss * 0.45;
    // The first stop takes a long spin, each next one a short one; a miss rests on screen for a second
    const stopped = () => cancelled.current;
    const play = async () => {
      for (let i = 0; i < stops.length; i++) {
        running.current = animate(rotation, turn(stops[i] as number, 4 + i), {
          duration: i === 0 ? firstSpin : spinOnFor,
          ease: [0.12, 0.8, 0.2, 1],
        });
        await running.current;
        if (stopped()) return;
        if (i === stops.length - 1) break;
        showMiss(i);
        await new Promise<void>((resolve) => {
          timer.current = window.setTimeout(resolve, missRest * 1000);
        });
        if (stopped()) return;
        spinOn();
      }
      land();
    };
    void play();
    return () => {
      cancelled.current = true;
      running.current?.stop();
      window.clearTimeout(timer.current);
    };
    // The rotation and the turns are fixed for one roll: the component is keyed by it
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rollId, reduce]);

  const playing = stage === 'spinning' || stage === 'miss';
  const miss = roll?.misses[missShown];
  const size = 300;
  const r = size / 2;
  const missSays = (m: WheelRoll['misses'][number]) =>
    m.playing ? t.missPlaying(m.by) : t.miss(m.by);
  const missLine = (m: WheelRoll['misses'][number]) => `${t.missNote(m.game.title)} ${missSays(m)}`;
  const picked = (pick: WheelRoll['pick']) =>
    pick.game ? t.result(pick.game.title) : t.choice(pick.choices ?? 0);

  return (
    <div className="grid w-full justify-items-center gap-3">
      <Table
        className="grid w-full max-w-190 cursor-default place-items-center gap-4 p-4 desk:grid-cols-2"
        onClick={() => {
          if (playing) finish();
        }}
      >
        <div className="w-75 max-w-full">
          <svg viewBox={`-10 -22 ${size + 20} ${size + 32}`} width="100%" aria-hidden>
            <motion.g style={{ rotate: rotation, originX: `${r}px`, originY: `${r}px` }}>
              {sectors.map((name, i) => {
                const a0 = ((i * sector - 90) * Math.PI) / 180;
                const a1 = (((i + 1) * sector - 90) * Math.PI) / 180;
                const midDeg = (i + 0.5) * sector - 90;
                const mid = (midDeg * Math.PI) / 180;
                const lx = r + r * 0.6 * Math.cos(mid);
                const ly = r + r * 0.6 * Math.sin(mid);
                // Labels run along the radius; on the left half they turn over to stay upright
                const tilt = Math.cos(mid) < 0 ? midDeg + 180 : midDeg;
                return (
                  <g key={name}>
                    <path
                      d={`M${r} ${r} L${r + r * Math.cos(a0)} ${r + r * Math.sin(a0)} A${r} ${r} 0 0 1 ${r + r * Math.cos(a1)} ${r + r * Math.sin(a1)} Z`}
                      fill={sectorFills[i % sectorFills.length]}
                      stroke="var(--color-ink)"
                      strokeWidth={3}
                    />
                    <text
                      x={lx}
                      y={ly}
                      transform={`rotate(${tilt} ${lx} ${ly})`}
                      className="font-display text-xs font-bold"
                      textAnchor="middle"
                      dominantBaseline="central"
                      fill="var(--color-ink)"
                    >
                      {name}
                    </text>
                  </g>
                );
              })}
            </motion.g>
            <circle
              cx={r}
              cy={r}
              r={26}
              fill="var(--color-card)"
              stroke="var(--color-ink)"
              strokeWidth={4}
            />
            <circle cx={r} cy={r} r={r} fill="none" stroke="var(--color-ink)" strokeWidth={6} />
            <path
              d={`M${r - 16} -18 L${r + 16} -18 L${r} 16 Z`}
              fill="var(--color-gold)"
              stroke="var(--color-ink)"
              strokeWidth={4}
              strokeLinejoin="round"
            />
          </svg>
        </div>
        {stage === 'miss' && miss ? (
          <motion.div
            initial={{ scale: 0.6, opacity: 0 }}
            animate={{ scale: 1, opacity: 1 }}
            transition={{ duration: 0.18 }}
          >
            <MomentCard className="w-full max-w-105">
              {miss.game.cover ? (
                <img
                  src={miss.game.cover}
                  alt=""
                  width={90}
                  height={135}
                  className="rounded-sm border-2 border-ink opacity-70 grayscale"
                />
              ) : null}
              <span className="line-clamp-2 text-sm text-ink-soft">{miss.game.title}</span>
              <strong>{missSays(miss)}</strong>
            </MomentCard>
          </motion.div>
        ) : null}
        {stage === 'done' && roll ? (
          <motion.div
            initial={reduce ? false : { rotateY: 90, opacity: 0 }}
            animate={{ rotateY: 0, opacity: 1 }}
            transition={{ duration: 0.25 }}
          >
            <MomentCard className="w-full max-w-105">
              {roll.pick.game?.cover ? (
                <img
                  src={roll.pick.game.cover}
                  alt=""
                  width={110}
                  height={165}
                  className="rounded-sm border-2 border-ink"
                />
              ) : null}
              <strong className="font-display text-xl font-heavy text-balance">
                {roll.pick.game ? roll.pick.game.title : picked(roll.pick)}
              </strong>
              <span className="text-sm text-ink-soft">
                {t.category(sectors[roll.pick.sector] ?? '')}
              </span>
              {roll.misses.map((m) => (
                <span key={m.game.title} className="line-clamp-2 text-xs text-ink-soft">
                  {missLine(m)}
                </span>
              ))}
            </MomentCard>
          </motion.div>
        ) : null}
      </Table>
      {/* The card shows the result; the words are for screen readers */}
      <p className="sr-only" aria-live="polite">
        {stage === 'done' && roll
          ? [
              ...roll.misses.map((miss) => `${missLine(miss)}.`),
              `${t.category(sectors[roll.pick.sector] ?? '')}. ${picked(roll.pick)}`,
            ].join(' ')
          : ''}
      </p>
    </div>
  );
}
