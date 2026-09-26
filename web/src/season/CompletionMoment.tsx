import { useReducedMotion } from 'motion/react';
import { useEffect, useRef, useState } from 'react';
import { DiceMoment, type DiceRoll } from '../board/Dice';
import { movePath } from '../board/geometry';
import type { MomentHandle } from '../board/moment';
import { TokenMove } from '../board/TokenMove';
import type { Board, Player } from '../board/types';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { cx } from '../ui/cx';

/** How long the total stays on the table before the token sets off (not with reduced motion) */
const totalRest = 600;

/**
 * A completion that came while the page is open (H4): the run's dice roll across the table, the total rests a moment,
 * then my token hops from its old cell to the new one. One skip shows the result of both. Everything it shows is
 * frozen when it starts, so a refresh of the season in the middle does not restart it; give it `key` by the run.
 */
export function CompletionMoment({
  dice,
  board,
  players,
  mover,
  from,
  to,
  fill = false,
  onDone,
}: {
  dice: DiceRoll;
  board: Board;
  players: Player[];
  /** Me, drawn on the path instead of on my cell */
  mover: Player;
  from: number;
  to: number;
  /** On a desktop the moment fills the map's stage */
  fill?: boolean;
  onDone: () => void;
}) {
  const reduce = useReducedMotion() ?? false;
  // Frozen at the start: new objects from a refresh must not restart the dice or the move
  const [roll] = useState(dice);
  const [path] = useState(() => movePath(board, from, to));
  const [token] = useState(() => ({ ...mover, cell: from }));
  const [step, setStep] = useState<'dice' | 'move'>('dice');
  const moment = useRef<MomentHandle>(null);
  const ended = useRef(false);
  const timer = useRef<number | undefined>(undefined);
  useEffect(
    () => () => {
      window.clearTimeout(timer.current);
    },
    [],
  );

  const end = () => {
    if (ended.current) return;
    ended.current = true;
    window.clearTimeout(timer.current);
    onDone();
  };

  return (
    <div
      data-testid="dice"
      className={cx(
        'grid w-full justify-items-center gap-3',
        fill ? 'h-full grid-rows-[minmax(0,1fr)_auto] content-stretch' : 'content-center',
      )}
    >
      {step === 'dice' ? (
        <div className={cx('grid w-full justify-items-center', fill && 'content-center')}>
          <DiceMoment
            ref={moment}
            roll={roll}
            announce={false}
            onPhase={(phase) => {
              if (phase !== 'done' || ended.current) return;
              if (!path) {
                end();
                return;
              }
              timer.current = window.setTimeout(
                () => {
                  setStep('move');
                },
                reduce ? 0 : totalRest,
              );
            }}
          />
        </div>
      ) : (
        <TokenMove
          ref={moment}
          board={board}
          players={players}
          mover={token}
          path={path}
          className={cx('w-full rounded-lg border-3 border-ink', fill ? 'h-full' : 'h-90')}
          onPhase={(phase) => {
            if (phase === 'done') end();
          }}
        />
      )}
      <Button
        onClick={() => {
          // The result of both at once: the dice and the token's cell
          if (ended.current) return;
          ended.current = true;
          window.clearTimeout(timer.current);
          moment.current?.skip();
          onDone();
        }}
      >
        {ru.moments.skip}
      </Button>
    </div>
  );
}
