import { LazyMotion } from 'motion/react';
import type { ReactNode } from 'react';
import { loadMotion } from './loadMotion';

const features = () => loadMotion().then((motion) => motion.domAnimation);

/** The animation features for the `m` components inside, loaded apart from the main page (loadMotion.ts) */
export function Motion({ children }: { children: ReactNode }) {
  return <LazyMotion features={features}>{children}</LazyMotion>;
}
