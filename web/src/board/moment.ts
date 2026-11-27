// The main moments (docs/DESIGN.md «Движение»): the roll of the wheel, the dice, the token's move, the finish. The only
// places with a show. Each shows a result the server already decided, skips on a tap or
// through its handle, shows the result at once with reduced motion, and says the result in words (aria-live).

import { diceMotion } from '../design/motion';

export type MomentPhase = 'idle' | 'playing' | 'done';

export type MomentHandle = { skip: () => void };

export { momentBudget } from '../design/motion';
export const diceSettleSeconds = diceMotion.settleSeconds;
