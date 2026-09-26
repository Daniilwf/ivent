import { ChevronDown } from 'lucide-react';
import {
  useId,
  type InputHTMLAttributes,
  type SelectHTMLAttributes,
  type TextareaHTMLAttributes,
} from 'react';
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

/** A choice from a list, the platform's own picker (on a phone, the system wheel): the label above, like Field */
export function Select({
  label,
  options,
  className,
  force,
  ...select
}: SelectHTMLAttributes<HTMLSelectElement> & {
  label: string;
  options: readonly { value: string; label: string }[];
  /** Styleguide only: shows the focus without the keyboard */
  force?: string;
}) {
  const id = useId();
  return (
    <div className={cx('grid gap-1', className)}>
      <label htmlFor={id} className="text-sm font-bold">
        {label}
      </label>
      <span className="relative grid">
        <select
          id={id}
          data-force={force}
          className="min-h-12 cursor-pointer appearance-none rounded-md border-2 border-ink bg-card pr-10 pl-3 text-base text-ink is-focus:outline-3 is-focus:outline-offset-2 is-focus:outline-ink"
          {...select}
        >
          {options.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
        <ChevronDown
          size={20}
          aria-hidden
          className="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2"
        />
      </span>
    </div>
  );
}

/**
 * One of a few short options as a row of pills: real radio buttons, so the arrows move between them and a screen
 * reader says «1 of 4»; the chosen pill is filled
 */
export function ChoiceGroup<T extends string>({
  legend,
  name,
  options,
  value,
  onChange,
  force,
}: {
  legend: string;
  name: string;
  options: readonly { value: T; label: string }[];
  value: T;
  onChange: (value: T) => void;
  /** Styleguide only: shows the focus on the chosen pill without the keyboard */
  force?: string;
}) {
  return (
    <fieldset className="grid gap-1">
      <legend className="mb-1 text-sm font-bold">{legend}</legend>
      <span className="flex flex-wrap gap-2">
        {options.map((option) => {
          const checked = option.value === value;
          return (
            <label
              key={option.value}
              data-force={checked ? force : undefined}
              className={cx(
                'inline-flex min-h-11 cursor-pointer items-center rounded-full border-2 border-ink px-4 font-medium transition duration-(--duration-fast) has-focus-visible:focus-ring is-focus:focus-ring',
                checked ? 'bg-ink text-on-color' : 'bg-card text-ink is-hover:bg-page',
              )}
            >
              <input
                type="radio"
                name={name}
                value={option.value}
                checked={checked}
                onChange={() => {
                  onChange(option.value);
                }}
                className="sr-only"
              />
              {option.label}
            </label>
          );
        })}
      </span>
    </fieldset>
  );
}
