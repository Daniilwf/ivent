import type { TargetAndTransition, Transition } from 'motion/react';

export const momentBudget = 3.5;

export const wheelMotion = {
  firstSpinSeconds: 3.2,
  loadingBudgetSeconds: 3.45,
  missPauseSeconds: 1,
  nextSpinSeconds: 1.4,
  visibleMisses: 2,
  reveal: { duration: 0.4, ease: [0.2, 0.7, 0.3, 1] } satisfies Transition,
  revealOffset: 8,
  ease: [0.45, 0.05, 0.25, 1] satisfies [number, number, number, number],
  // Maximum dy/dx of the easing curve, rounded up to keep the speed below the cap.
  peakSlope: 2.58,
  maxDegreesPerSecond: 720,
};

export const diceMotion = {
  faceSeconds: 1.4,
  settleSeconds: 1.6,
  totalRestSeconds: 0.8,
  maxStaggerSeconds: 0.09,
  degrees: 360,
  spring: { type: 'spring', stiffness: 70, damping: 14, mass: 0.9 } satisfies Transition,
  ease: [0.2, 0.7, 0.3, 1] satisfies [number, number, number, number],
};

export const cameraFrameSeconds = 0.6;
export const movePathSeconds = 3;
export const moveMotion = {
  hopSeconds: 0.32,
  minHopSeconds: 0.2,
  hopHeight: 22,
  pathPadding: 52,
  cameraScreensPerSecond: 0.5,
};

export const confettiMotion = { seconds: 2.6, count: 70 };

export const eventDrawDelaySeconds = 0.3;
export const eventDrawBudgetSeconds = 1.5;

/**
 * A fresh event card comes off the deck and turns face up (DESIGN.md).
 * Motion takes seconds and a bezier tuple; tokens.test.ts keeps these values equal to the CSS tokens.
 */
export const eventDraw = {
  initial: { rotateY: -60, y: -24, scale: 0.92, opacity: 0 } satisfies TargetAndTransition,
  animate: { rotateY: 0, y: 0, scale: 1, opacity: 1 } satisfies TargetAndTransition,
  transition: { duration: 1.1, ease: [0.2, 0.7, 0.3, 1] } satisfies Transition,
  instantTransition: { duration: 0 } satisfies Transition,
};
