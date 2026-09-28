import { useReducedMotion } from 'motion/react';
import { useEffect, useRef, useState } from 'react';
import { DiceMoment, type DiceRoll } from '../board/Dice';
import { movePath } from '../board/geometry';
import { momentBudget, type MomentHandle } from '../board/moment';
import { TokenMove } from '../board/TokenMove';
import type { Board, Player } from '../board/types';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { cx } from '../ui/cx';

/** How long the total stays on the table before the token sets off (not with reduced motion) */
const totalRest = 300;
/** The dice settle in 1.5 s; the rest and the walk fit the moments' budget with them (docs/DESIGN.md: under 3 s) */
const walkBudget = momentBudget - 1.5 - totalRest / 1000;

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
  free = false,
  walked,
  onDone,
}: {
  /** The cells the token passed, as the server walked them (the graph map, D-304): its old cell first, the flights
   *  (teleports) by the index they land on; without it the token walks the chain from `from` to `to` */
  walked?: { path: number[] | null; jumps: number[] } | undefined;
  /** The frozen first plays in free mode: the dice give no points */
  free?: boolean;
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
  const [path] = useState(() => (walked ? walked.path : movePath(board, from, to)));
  const [jumps] = useState(() => walked?.jumps ?? []);
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
        'grid w-full justify-items-center',
        fill ? 'relative h-full' : 'content-center gap-3',
      )}
    >
      {step === 'dice' ? (
        <div className={cx('grid w-full justify-items-center', fill && 'h-full')}>
          <DiceMoment
            fill={fill}
            ref={moment}
            roll={roll}
            announce={false}
            mode={free ? 'free' : path ? 'move' : 'stay'}
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
          jumps={jumps}
          budget={walkBudget}
          className={cx('w-full', fill ? 'h-full' : 'h-90 rounded-lg border-3 border-ink')}
          onPhase={(phase) => {
            if (phase === 'done') end();
          }}
        />
      )}
      <Button
        className={fill ? 'absolute bottom-4 left-1/2 z-10 -translate-x-1/2' : undefined}
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
