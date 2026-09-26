import {
  useId,
  type InputHTMLAttributes,
  type SelectHTMLAttributes,
  type TextareaHTMLAttributes,
} from 'react';
import { cx } from './cx';

/** A choice from a list: the label above, like Field; the native list works on a phone and with a keyboard */
export function SelectField({
  label,
  hint,
  error,
  className,
  children,
  ...select
}: SelectHTMLAttributes<HTMLSelectElement> & {
  label: string;
  hint?: string;
  error?: string | undefined;
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
      <select
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={cx(hint && hintId, error && errorId) || undefined}
        className={cx(
          'min-h-12 w-full min-w-0 cursor-pointer rounded-md border-2 bg-card px-3 text-base text-ink disabled:cursor-default disabled:bg-page disabled:opacity-60',
          'is-focus:outline-3 is-focus:outline-offset-2 is-focus:outline-ink',
          error ? 'border-danger' : 'border-ink',
        )}
        {...select}
      >
        {children}
      </select>
      {error ? (
        <span id={errorId} role="alert" className="text-sm font-medium text-danger">
          {error}
        </span>
      ) : null}
    </div>
  );
}

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
        <span id={errorId} role="alert" className="text-sm font-medium text-danger">
          {error}
        </span>
      ) : null}
    </div>
  );
}

/** A longer text: the label above, the error under it, like Field */
export function TextArea({
  label,
  hint,
  error,
  className,
  ...input
}: TextareaHTMLAttributes<HTMLTextAreaElement> & {
  label: string;
  hint?: string;
  error?: string | undefined;
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
      <textarea
        id={id}
        rows={4}
        aria-invalid={error ? true : undefined}
        aria-describedby={cx(hint && hintId, error && errorId) || undefined}
        className={cx(
          'min-h-28 rounded-md border-2 bg-card px-3 py-2 text-base text-ink placeholder:text-ink-soft disabled:bg-page',
          error ? 'border-danger' : 'border-ink',
        )}
        {...input}
      />
      {error ? (
        <span id={errorId} role="alert" className="text-sm font-medium text-danger">
          {error}
        </span>
      ) : null}
    </div>
  );
}

/** A yes-or-no choice with its label to the right; the whole row takes the tap */
export function Checkbox({
  label,
  className,
  ...input
}: Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> & { label: string }) {
  return (
    <label className={cx('flex min-h-11 cursor-pointer items-center gap-3 text-base', className)}>
      <input type="checkbox" className="size-5 shrink-0 accent-action" {...input} />
      {label}
    </label>
  );
}
