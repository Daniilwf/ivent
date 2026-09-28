import type { Schemas } from '../api/client';

export type TechRerollReason = NonNullable<Schemas['TechRerollReason']>;

/** The reasons of a tech reroll, in the order the forms offer them (the player's and the admin's) */
export const techRerollReasons: readonly TechRerollReason[] = [
  'weakPc',
  'paidUnavailable',
  'doesNotLaunch',
  'emulatorTooSlow',
  'other',
];
