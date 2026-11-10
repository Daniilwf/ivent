// Tests only: how long a test waits for what a form shows once the typing rests. The form asks the server only after
// its own delay (the pool's alike titles, the map's check), so Testing Library's usual second (`findBy…`, `waitFor`)
// would be mostly spent on that delay: on a loaded machine the answer came a little after it (FL-2). The wait is the
// form's delay plus the usual second for the answer and the render, not a longer budget for the rest of the test.

/** Testing Library's default wait (asyncUtilTimeout) */
const usualWaitMs = 1000;

/** The options of a `findBy…` or `waitFor` that waits through a form's delay of `delayMs` */
export const pastTheDelay = (delayMs: number) => ({ timeout: delayMs + usualWaitMs });
