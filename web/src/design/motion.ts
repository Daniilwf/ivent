import type { TargetAndTransition, Transition } from 'motion/react';

/**
 * A fresh event card comes off the deck and turns face up, a main moment under 3 s (DESIGN.md).
 * Motion takes seconds and a bezier tuple; tokens.test.ts keeps these values equal to the CSS tokens.
 */
export const eventDraw = {
  initial: { rotateY: -90, y: -24, scale: 0.92, opacity: 0 } satisfies TargetAndTransition,
  animate: { rotateY: 0, y: 0, scale: 1, opacity: 1 } satisfies TargetAndTransition,
  transition: { duration: 0.9, ease: [0.2, 0.7, 0.3, 1] } satisfies Transition,
  instantTransition: { duration: 0 } satisfies Transition,
};
