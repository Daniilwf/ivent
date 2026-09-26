import { LoaderCircle } from 'lucide-react';
import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { ru } from '../i18n/ru';
import { cx } from './cx';

export type ButtonVariant = 'main' | 'quiet' | 'danger' | 'dangerMain' | 'link' | 'dangerLink';

const base = cx(
  'inline-flex cursor-pointer items-center justify-center gap-2 font-bold whitespace-nowrap transition duration-(--duration-fast) ease-out select-none disabled:cursor-default disabled:opacity-50 aria-disabled:cursor-default aria-disabled:opacity-50',
);

const variants: Record<ButtonVariant, string> = {
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

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  /** Waiting for the server: the button keeps its label and size, shows a spinner and takes no clicks */
  loading?: boolean;
  icon?: ReactNode;
  /** Styleguide only: shows a state without the pointer («hover», «focus», «active») */
  force?: string;
};

export function Button({
  variant = 'quiet',
  loading = false,
  icon,
  force,
  className,
  children,
  disabled,
  type = 'button',
  ...rest
}: ButtonProps) {
  return (
    <button
      type={type}
      className={cx(base, variants[variant], className)}
      disabled={disabled ?? false}
      aria-disabled={loading || undefined}
      aria-busy={loading || undefined}
      data-force={force}
      data-variant={variant}
      {...rest}
      onClick={loading ? undefined : rest.onClick}
    >
      {loading ? (
        <LoaderCircle size={20} className="animate-spin" aria-label={ru.ui.loading} />
      ) : (
        icon
      )}
      {children}
    </button>
  );
}

/** A round button with an icon only: the label is for screen readers and the tooltip */
export function IconButton({
  label,
  children,
  force,
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { label: string; force?: string }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      data-force={force}
      className={cx(
        'inline-grid size-11 cursor-pointer place-items-center rounded-full border-2 border-ink bg-card text-ink transition duration-(--duration-fast) is-hover:bg-page is-active:translate-y-px disabled:cursor-default disabled:opacity-50',
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}
