import { useId, type InputHTMLAttributes } from 'react';
import { cx } from './cx';

/** A form field: the label above, a hint about the format, the error right under the field */
export function Field({
  label,
  hint,
  error,
  force,
  className,
  ...input
}: InputHTMLAttributes<HTMLInputElement> & {
  label: string;
  hint?: string;
  error?: string | undefined;
  /** Styleguide only: shows the focus without the keyboard */
  force?: string;
}) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  return (
    <div className={cx('grid gap-1', className)}>
      <label htmlFor={id} className="text-sm font-bold">
        {label}
      </label>
      {hint ? (
        <span id={hintId} className="text-sm text-ink-soft">
          {hint}
        </span>
      ) : null}
      <input
        id={id}
        data-force={force}
        aria-invalid={error ? true : undefined}
        aria-describedby={cx(hint && hintId, error && errorId) || undefined}
        className={cx(
          'min-h-12 rounded-md border-2 bg-card px-3 text-base text-ink transition duration-(--duration-fast) placeholder:text-ink-soft disabled:bg-page disabled:opacity-60',
          'is-focus:outline-3 is-focus:outline-offset-2 is-focus:outline-ink',
          error ? 'border-danger' : 'border-ink',
        )}
        {...input}
      />
      {error ? (
        <span id={errorId} className="text-sm font-medium text-danger">
          {error}
        </span>
      ) : null}
    </div>
  );
}
