import { useRef, useState } from 'react';
import { Confetti } from '../board/Confetti';
import { DiceMoment, type DiceRoll } from '../board/Dice';
import { cellById, walk } from '../board/geometry';
import { MapView } from '../board/MapView';
import type { MomentHandle, MomentPhase } from '../board/moment';
import { TokenMove } from '../board/TokenMove';
import { WheelMoment, type WheelRoll } from '../board/Wheel';
import { demoBoard } from '../board/demoBoard';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Chip } from '../ui/Marks';
import { MomentCard } from '../ui/Surface';
import { Sticker } from '../ui/Sticker';
import { demoCategories, demoGames, demoLeader, demoMe, demoPlayers } from './fixtures';

const t = ru.styleguide;
const m = ru.moments;
const me = demoMe;
const hint = <p className="text-center text-sm text-ink-soft">{m.skipHint}</p>;

/** The map on a desktop frame and on a phone frame side by side */
export function MapDemo() {
  return (
    <div className="grid gap-4 desk:grid-cols-[minmax(0,1fr)_auto]">
      <MapView
        board={demoBoard}
        players={demoPlayers}
        className="h-150 rounded-lg border-3 border-ink"
      />
      <div className="grid justify-items-center gap-2">
        <MapView
          board={demoBoard}
          players={demoPlayers}
          focus={me.cell}
          tools="top"
          className="h-150 w-full max-w-90 rounded-lg border-3 border-ink"
        />
        <span className="text-sm text-ink-soft">{t.map.phone}</span>
      </div>
    </div>
  );
}

// Each demo keeps the moment's phase: while it plays, the main button shows the result at once

export function WheelDemo() {
  const [roll, setRoll] = useState<WheelRoll | null>(null);
  const [phase, setPhase] = useState<MomentPhase>('idle');
  const moment = useRef<MomentHandle>(null);
  const spin = () => {
    const pick = Math.floor(Math.random() * demoCategories.length);
    const miss = (pick + 3 + Math.floor(Math.random() * 4)) % demoCategories.length;
    setRoll({
      id: (roll?.id ?? 0) + 1,
      misses: [
        {
          sector: miss,
          game: demoGames[0] as (typeof demoGames)[number],
          by: demoPlayers[1]?.name ?? '',
        },
      ],
      pick: { sector: pick, game: demoGames[3] as (typeof demoGames)[number] },
    });
    setPhase('playing');
  };
  return (
    <div className="grid justify-items-center gap-3">
      <WheelMoment
        key={roll?.id ?? 0}
        ref={moment}
        sectors={demoCategories}
        roll={roll}
        onPhase={setPhase}
      />
      <div className="flex flex-wrap justify-center gap-3">
        {phase === 'playing' ? (
          <Button variant="main" onClick={() => moment.current?.skip()}>
            {m.skip}
          </Button>
        ) : phase === 'done' ? (
          <>
            <Button
              variant="main"
              onClick={() => {
                setRoll(null);
                setPhase('idle');
              }}
            >
              {t.wheel.start}
            </Button>
            <Button onClick={spin}>{t.wheel.again}</Button>
          </>
        ) : (
          <Button variant="main" onClick={spin}>
            {t.wheel.spin}
          </Button>
        )}
      </div>
      {hint}
    </div>
  );
}

export function DiceDemo() {
  const [roll, setRoll] = useState<DiceRoll | null>(null);
  const [phase, setPhase] = useState<MomentPhase>('idle');
  const moment = useRef<MomentHandle>(null);
  const throwDice = () => {
    setRoll({
      id: (roll?.id ?? 0) + 1,
      values: [0, 0, 0, 0].map(() => 1 + Math.floor(Math.random() * 6)),
      challenge: true,
    });
    setPhase('playing');
  };
  return (
    <div className="grid justify-items-center gap-3">
      <DiceMoment key={roll?.id ?? 0} ref={moment} roll={roll} onPhase={setPhase} />
      <Button
        variant="main"
        onClick={phase === 'playing' ? () => moment.current?.skip() : throwDice}
      >
        {phase === 'playing' ? m.skip : roll ? t.dice.again : t.dice.roll}
      </Button>
      {hint}
    </div>
  );
}

const moveFrom = 9;
const moveSteps =
  [7, 8, 6, 9, 5].find(
    (n) =>
      cellById(demoBoard, walk(demoBoard, moveFrom, n, 'second').at(-1) ?? moveFrom).kind ===
      'event',
  ) ?? 7;
const movePath = walk(demoBoard, moveFrom, moveSteps, 'second');

export function MoveDemo() {
  const [run, setRun] = useState(0);
  const [phase, setPhase] = useState<MomentPhase>('idle');
  const moment = useRef<MomentHandle>(null);
  const mover = { ...me, cell: moveFrom };
  const players = demoPlayers.map((p) => (p.me ? mover : p));
  const landed = movePath.at(-1) ?? moveFrom;
  return (
    <div className="grid justify-items-center gap-3">
      <div className="relative w-full max-w-140 overflow-hidden rounded-lg border-3 border-ink">
        <TokenMove
          key={run}
          ref={moment}
          board={demoBoard}
          players={players}
          mover={mover}
          path={run > 0 ? movePath : null}
          className="h-105"
          onPhase={setPhase}
        />
        {phase !== 'idle' ? (
          <span className="absolute top-3 left-1/2 -translate-x-1/2">
            <Chip>{t.move.fork}</Chip>
          </span>
        ) : null}
        {phase === 'done' ? (
          <MomentCard className="absolute inset-x-3 bottom-3 bg-gold">
            <strong>{t.move.landed(landed)}</strong>
          </MomentCard>
        ) : null}
      </div>
      <p className="sr-only" aria-live="polite">
        {phase === 'done' ? t.move.landed(landed) : ''}
      </p>
      <div className="flex flex-wrap justify-center gap-3">
        <Button
          variant="main"
          onClick={() => {
            if (phase === 'playing') {
              moment.current?.skip();
              return;
            }
            setRun((r) => r + 1);
            setPhase('playing');
          }}
        >
          {phase === 'playing' ? m.skip : t.move.go(moveSteps)}
        </Button>
        {phase === 'done' ? (
          <Button
            onClick={() => {
              setRun(0);
              setPhase('idle');
            }}
          >
            {t.move.reset}
          </Button>
        ) : null}
      </div>
      {hint}
    </div>
  );
}

const finishId = demoBoard.cells.find((c) => c.kind === 'finish')?.id ?? 1;
const finishFrom =
  demoBoard.cells.find((c) => {
    const path = walk(demoBoard, c.id, 4);
    return path.length === 5 && path.at(-1) === finishId;
  })?.id ?? finishId;
const finishPath = walk(demoBoard, finishFrom, 4);

export function FinishDemo() {
  const [run, setRun] = useState(0);
  const [phase, setPhase] = useState<MomentPhase>('idle');
  const moment = useRef<MomentHandle>(null);
  const winner = { ...demoLeader, cell: finishFrom };
  const players = demoPlayers.map((p, i) => (i === 0 ? winner : p));
  return (
    <div className="grid justify-items-center gap-3">
      <div className="relative w-full max-w-140 overflow-hidden rounded-lg border-3 border-ink">
        <TokenMove
          key={run}
          ref={moment}
          board={demoBoard}
          players={players}
          mover={winner}
          path={run > 0 ? finishPath : null}
          size={40}
          className="h-105"
          onPhase={setPhase}
        />
        {phase === 'done' ? <Confetti key={run} /> : null}
      </div>
      {phase === 'done' ? (
        <MomentCard className="w-full max-w-105">
          <Sticker player={winner} size={72} />
          <strong className="font-display text-xl font-heavy text-balance">
            {m.finish.first(winner.name)}
          </strong>
          <Chip>{m.finish.provisional}</Chip>
          <span className="text-sm text-ink-soft">{m.finish.frozen}</span>
        </MomentCard>
      ) : null}
      <p className="sr-only" aria-live="polite">
        {phase === 'done' ? `${m.finish.first(winner.name)} ${m.finish.provisional}` : ''}
      </p>
      <Button
        variant="main"
        onClick={() => {
          if (phase === 'playing') {
            moment.current?.skip();
            return;
          }
          setRun((r) => r + 1);
          setPhase('playing');
        }}
      >
        {phase === 'playing' ? m.skip : phase === 'done' ? t.finish.again : t.finish.go}
      </Button>
      {hint}
    </div>
  );
}
