import { cx } from './cx';

export type ButtonVariant = 'main' | 'quiet' | 'danger' | 'dangerMain' | 'link' | 'dangerLink';

export const base = cx(
  'inline-flex cursor-pointer items-center justify-center gap-2 font-bold whitespace-nowrap transition duration-(--duration-fast) ease-out select-none is-focus:focus-ring disabled:cursor-default disabled:not-aria-busy:opacity-50 aria-busy:cursor-progress',
);

export const variants: Record<ButtonVariant, string> = {
  // The one main action of a screen: a blue meeple with a cardboard shadow
  main: cx(
    'min-h-13 rounded-full border-3 border-ink bg-action px-6 text-lg text-on-color shadow-press is-hover:-translate-y-px is-hover:bg-action-strong is-hover:shadow-press-up is-active:translate-y-1 is-active:shadow-press-down',
  ),
  quiet: cx(
    'min-h-12 rounded-full border-2 border-ink bg-card px-5 text-base text-ink is-hover:bg-page is-active:translate-y-px',
  ),
  // Dangerous actions are red, outlined until the confirmation, filled in it
  danger: cx(
    'min-h-12 rounded-full border-2 border-danger bg-card px-5 text-base text-danger is-hover:bg-danger-soft is-active:translate-y-px',
  ),
  dangerMain: cx(
    'min-h-12 rounded-full border-3 border-ink bg-danger px-5 text-base text-on-color shadow-press is-hover:-translate-y-px is-hover:shadow-press-up is-active:translate-y-1 is-active:shadow-press-down',
  ),
  link: cx('min-h-11 px-1 text-base text-ink underline underline-offset-4 is-hover:decoration-2'),
  dangerLink: cx(
    'min-h-11 px-1 text-base text-danger underline underline-offset-4 is-hover:decoration-2',
  ),
};

/**
 * The look of a button for what is a link underneath (a download, a page of the site, an outside address): one set of
 * variants for both, so a link never copies a button's classes by hand (D-202)
 */
export function buttonClass(variant: ButtonVariant = 'quiet', className?: string) {
  return cx(base, variants[variant], className);
}

/** A name inside a text that leads to its page (a player, a game): underlined, darker on hover */
export const inlineLink =
  'rounded-sm underline decoration-2 decoration-muted underline-offset-4 is-hover:decoration-ink is-focus:focus-ring';
