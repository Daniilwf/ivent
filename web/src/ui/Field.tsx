import { ImagePlus, LoaderCircle } from 'lucide-react';
import {
  useId,
  type InputHTMLAttributes,
  type ReactNode,
  type Ref,
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
  ref?: Ref<HTMLTextAreaElement> | undefined;
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

const control = cx(
  'min-h-12 rounded-md border-2 bg-card px-3 text-base text-ink transition duration-(--duration-fast) disabled:bg-page disabled:opacity-60',
  'is-focus:outline-3 is-focus:outline-offset-2 is-focus:outline-ink',
);

/** A pick from a short list: the phone's own picker, in the fields' frame; the label above, the error under it */
export function Select({
  label,
  hint,
  error,
  force,
  className,
  children,
  ...select
}: SelectHTMLAttributes<HTMLSelectElement> & {
  label: string;
  hint?: string;
  error?: string | undefined;
  force?: string;
  children: ReactNode;
  ref?: Ref<HTMLSelectElement> | undefined;
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
        data-force={force}
        aria-invalid={error ? true : undefined}
        aria-describedby={cx(hint && hintId, error && errorId) || undefined}
        className={cx(control, 'w-full cursor-pointer', error ? 'border-danger' : 'border-ink')}
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

/** One of a few answers, all in sight: pills in a row that wrap on a phone (radio buttons underneath) */
export function ChoiceGroup<T extends string>({
  label,
  hint,
  options,
  value,
  onChange,
  disabled = false,
  force,
  className,
  ...rest
}: {
  label: string;
  hint?: string;
  options: readonly { value: T; label: string }[];
  value: T;
  onChange: (value: T) => void;
  disabled?: boolean;
  /** Styleguide only: forces a state on the picked pill */
  force?: string;
  className?: string;
  'data-testid'?: string;
}) {
  const name = useId();
  const hintId = `${name}-hint`;
  return (
    <fieldset
      className={cx('grid min-w-0 gap-1', className)}
      aria-describedby={hint ? hintId : undefined}
      disabled={disabled}
      {...rest}
    >
      <legend className="mb-1 text-sm font-bold">{label}</legend>
      {hint ? (
        <span id={hintId} className="text-sm text-ink-soft">
          {hint}
        </span>
      ) : null}
      <div className="flex flex-wrap gap-2">
        {options.map((option) => {
          const picked = option.value === value;
          return (
            <label
              key={option.value}
              data-force={picked ? force : undefined}
              className={cx(
                'inline-flex min-h-11 cursor-pointer items-center rounded-full border-2 border-ink px-4 text-base font-medium whitespace-nowrap transition duration-(--duration-fast) select-none has-[:focus-visible]:focus-ring',
                'has-[:disabled]:cursor-default has-[:disabled]:opacity-50',
                picked ? 'bg-ink text-card' : 'bg-card text-ink is-hover:bg-page',
                force?.includes('focus') && picked ? 'focus-ring' : undefined,
              )}
            >
              <input
                type="radio"
                name={name}
                value={option.value}
                checked={picked}
                onChange={() => {
                  onChange(option.value);
                }}
                className="sr-only"
              />
              {option.label}
            </label>
          );
        })}
      </div>
    </fieldset>
  );
}

/** Picking a file with a button in Russian: the browser's own field (English «Choose File») stays hidden under it */
export function FilePicker({
  label,
  hint,
  busy = false,
  force,
  className,
  ref,
  ...input
}: Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> & {
  label: string;
  hint?: string;
  /** Uploading: the button spins and takes no new file */
  busy?: boolean;
  force?: string;
  ref?: Ref<HTMLInputElement> | undefined;
}) {
  const id = useId();
  const hintId = `${id}-hint`;
  const off = busy || input.disabled === true;
  return (
    <div className={cx('grid justify-items-start gap-1', className)}>
      <label
        htmlFor={id}
        data-force={force}
        aria-busy={busy || undefined}
        className={cx(
          'inline-flex min-h-12 cursor-pointer items-center justify-center gap-2 rounded-full border-2 border-ink bg-card px-5 text-base font-bold whitespace-nowrap text-ink transition duration-(--duration-fast) select-none has-[:focus-visible]:focus-ring is-hover:bg-page',
          force?.includes('focus') ? 'focus-ring' : undefined,
          off
            ? 'cursor-default opacity-50 aria-busy:cursor-progress aria-busy:opacity-100'
            : undefined,
        )}
      >
        {busy ? (
          <LoaderCircle size={20} className="animate-spin motion-reduce:animate-none" aria-hidden />
        ) : (
          <ImagePlus size={20} aria-hidden />
        )}
        {label}
        <input
          ref={ref}
          id={id}
          type="file"
          className="sr-only"
          aria-describedby={hint ? hintId : undefined}
          {...input}
          disabled={off}
        />
      </label>
      {hint ? (
        <span id={hintId} className="text-sm text-ink-soft">
          {hint}
        </span>
      ) : null}
    </div>
  );
}
