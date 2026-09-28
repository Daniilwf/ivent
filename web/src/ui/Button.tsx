import { LoaderCircle } from 'lucide-react';
import type { ButtonHTMLAttributes, ReactNode, Ref } from 'react';
import { ru } from '../i18n/ru';
import { base, variants, type ButtonVariant } from './buttonStyles';
import { cx } from './cx';

export type { ButtonVariant } from './buttonStyles';

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  /** Waiting for the server: the button keeps its label and size, shows a spinner and takes no clicks */
  loading?: boolean;
  icon?: ReactNode;
  /** Styleguide only: shows a state without the pointer («hover», «focus», «active») */
  force?: string;
  ref?: Ref<HTMLButtonElement> | undefined;
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
      onClick={
        loading
          ? (e) => {
              e.preventDefault();
            }
          : rest.onClick
      }
    >
      {loading ? (
        <LoaderCircle
          size={20}
          className="animate-spin motion-reduce:animate-none"
          aria-label={ru.ui.loading}
        />
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
  loading = false,
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  label: string;
  force?: string;
  /** Busy: a spinner instead of the icon; the button keeps its focus and takes no clicks */
  loading?: boolean;
  ref?: Ref<HTMLButtonElement> | undefined;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      data-force={force}
      aria-busy={loading || undefined}
      className={cx(
        'inline-grid size-11 shrink-0 cursor-pointer aria-busy:cursor-progress place-items-center rounded-full border-2 border-ink bg-card text-ink transition duration-(--duration-fast) is-hover:bg-page is-focus:focus-ring is-active:translate-y-px disabled:cursor-default disabled:opacity-50',
        className,
      )}
      {...rest}
      onClick={loading ? undefined : rest.onClick}
    >
      {loading ? (
        <LoaderCircle size={20} className="animate-spin motion-reduce:animate-none" aria-hidden />
      ) : (
        children
      )}
    </button>
  );
}
