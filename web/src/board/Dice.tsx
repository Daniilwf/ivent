import { motion, useReducedMotion } from 'motion/react';
import { useEffect, useEffectEvent, useImperativeHandle, useRef, useState, type Ref } from 'react';
import { ru } from '../i18n/ru';
import './board.css';
import { cx } from '../ui/cx';
import { Table } from '../ui/Surface';
import type { MomentHandle, MomentPhase } from './moment';

const t = ru.moments.dice;

/** The server's throw: every die's value; the last `challenge` of them are the challenge dice */
export type DiceRoll = { id: number; values: number[]; challenge: number };

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
const faces = [
  { value: 1, place: 'die-f1' },
  { value: 6, place: 'die-f6' },
  { value: 3, place: 'die-f3' },
  { value: 4, place: 'die-f4' },
  { value: 2, place: 'die-f2' },
  { value: 5, place: 'die-f5' },
];

function Face({
  value,
  place,
  challenge,
  number,
}: {
  value: number;
  place: string;
  challenge: boolean;
  /** A die of more than six sides shows its number instead of pips */
  number?: number | undefined;
}) {
  if (number !== undefined)
    return (
      <span
        className={cx(
          'absolute inset-0 grid place-items-center rounded-md border-3 border-ink font-display text-2xl font-heavy backface-hidden',
          challenge ? 'bg-gold' : 'bg-card',
          place,
        )}
      >
        {number}
      </span>
    );
  return (
    <span
      className={cx(
        'absolute inset-0 grid grid-cols-3 grid-rows-3 rounded-md border-3 border-ink p-2 backface-hidden',
        challenge ? 'bg-gold' : 'bg-card',
        place,
      )}
    >
      {Array.from({ length: 9 }, (_, i) => (
        <span
          key={i}
          className={cx(
            'size-2 place-self-center rounded-full',
            pips[value]?.includes(i) && 'bg-ink',
          )}
        />
      ))}
    </span>
  );
}

/** A die thrown in from the left edge of the table: it tumbles, bounces and settles on its face */
export function Die({
  value,
  index = 0,
  thrown = false,
  challenge = false,
}: {
  value: number;
  index?: number;
  thrown?: boolean;
  challenge?: boolean;
}) {
  const [x, y] = faceTurn[value] ?? [0, 0];
  const delay = index * 0.09;
  return (
    <span className="inline-grid justify-items-center gap-2" data-die={value}>
      <motion.span
        className="block"
        initial={thrown ? { x: -260 - index * 30, y: -90, rotate: -200 } : false}
        animate={{ x: 0, y: 0, rotate: 0 }}
        transition={
          thrown
            ? { type: 'spring', stiffness: 110, damping: 11, mass: 0.9, delay }
            : { duration: 0 }
        }
      >
        <motion.span
          className="relative block size-16 transform-3d"
          initial={thrown ? { rotateX: 0, rotateY: 0 } : false}
          animate={{ rotateX: x + 720, rotateY: y + 720 }}
          transition={thrown ? { duration: 1.2, delay, ease: [0.2, 0.7, 0.3, 1] } : { duration: 0 }}
        >
          {faces.map((f) => (
            <Face
              key={f.value}
              value={f.value}
              place={f.place}
              challenge={challenge}
              number={value in pips || f.value !== 1 ? undefined : value}
            />
          ))}
        </motion.span>
      </motion.span>
      {challenge ? (
        <span className="rounded-full bg-card px-2 text-xs font-bold">{t.challengeDie}</span>
      ) : null}
    </span>
  );
}

/** The throw after a completed run: the dice roll across the table, then the total. Give it `key={roll.id}`:
 *  every throw is a fresh moment. */
export function DiceMoment({
  roll,
  onPhase,
  ref,
  announce = true,
  stay = false,
  fill = false,
}: {
  /** The dice fill their frame (the map's stage on a desktop): the table is the whole frame */
  fill?: boolean;
  roll: DiceRoll | null;
  onPhase?: ((phase: MomentPhase) => void) | undefined;
  ref?: Ref<MomentHandle> | undefined;
  /** False when the page has its own live region for the result (it outlives the dice) */
  announce?: boolean;
  /** The token stays where it is (a later finisher): the dice give points only */
  stay?: boolean;
}) {
  const reduce = useReducedMotion() ?? false;
  const [shown, setShown] = useState(roll === null);
  const [skipped, setSkipped] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  // The end is reported once, whether the dice settle or the moment is skipped
  const ended = useRef(false);
  const settle = useEffectEvent(() => {
    if (ended.current) return;
    ended.current = true;
    setShown(true);
    onPhase?.('done');
  });

  const finish = () => {
    if (ended.current || !roll) return;
    ended.current = true;
    window.clearTimeout(timer.current);
    setSkipped(true);
    setShown(true);
    onPhase?.('done');
  };
  useImperativeHandle(ref, () => ({ skip: finish }));

  useEffect(() => {
    if (!roll) return;
    timer.current = window.setTimeout(settle, reduce ? 0 : 1500);
    return () => {
      window.clearTimeout(timer.current);
    };
  }, [roll, reduce]);

  const values = roll?.values ?? [6, 6, 6];
  const total = values.reduce((a, b) => a + b, 0);
  const extra = roll?.challenge ?? 0;
  const plain = values.slice(0, values.length - extra);
  const challenge = values.slice(values.length - extra);
  const thrown = roll !== null && !skipped && !reduce;

  const content = (
    <>
      <div className="flex flex-wrap items-start justify-center gap-3">
        {values.map((v, i) => (
          <Die
            key={`${skipped ? 's' : 'r'}-${i}`}
            value={v}
            index={i}
            thrown={thrown}
            challenge={i >= values.length - extra}
          />
        ))}
      </div>
      {shown && roll ? (
        <motion.span
          className="rounded-full border-3 border-ink bg-card px-6 font-display text-3xl font-heavy"
          initial={reduce ? false : { scale: 0.5, opacity: 0 }}
          animate={{ scale: 1, opacity: 1 }}
          transition={{ type: 'spring', stiffness: 400, damping: 18 }}
        >
          +{total}
        </motion.span>
      ) : null}
    </>
  );
  const words = (
    <p
      className={cx(
        'min-h-5 text-center text-sm text-balance',
        fill ? 'rounded-md bg-card px-3 py-1 text-ink empty:hidden' : 'text-ink-soft',
      )}
      aria-live={announce ? 'polite' : undefined}
    >
      {shown && roll ? (stay ? t.resultStay : t.result)(plain, challenge, total) : ''}
    </p>
  );
  if (fill)
    return (
      <div
        className="table-surface grid h-full w-full content-center justify-items-center gap-6 px-4 pt-6 pb-24 perspective-distant"
        onClick={finish}
      >
        {content}
        {words}
      </div>
    );

  return (
    <div className="grid w-full justify-items-center gap-3">
      <Table
        className="grid min-h-90 w-full max-w-140 content-center justify-items-center gap-6 px-4 py-6 perspective-distant"
        onClick={finish}
      >
        {content}
      </Table>
      {words}
    </div>
  );
}
