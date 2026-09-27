import { GitFork } from 'lucide-react';
import type { BranchOption } from '../board/graphBoard';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';
import { Tag } from '../ui/Marks';

const t = ru.map.branch;

/** The branch choice's heading takes the focus when it appears, as a rolled game's does */
export const branchTitle = 'branch-title';

/**
 * The branch choice at a fork (D-304): the steps left and one big button per branch, numbered as the rings on the
 * map. On a desktop it stands on the map's stage, on a phone in the turn card; every option is a button, so the
 * keyboard picks one as the pointer does.
 */
export function BranchChoice({
  steps,
  options,
  pending,
  onChoose,
  className,
}: {
  steps: number;
  options: BranchOption[];
  pending: boolean;
  onChoose: (optionId: string) => void;
  className?: string;
}) {
  return (
    <fieldset
      data-testid="branch"
      aria-busy={pending || undefined}
      className={cx('grid min-w-0 gap-3', className)}
    >
      <legend className="mb-1 font-display text-lg font-heavy">
        <span id={branchTitle} tabIndex={-1} className="flex items-center gap-2 focus:outline-none">
          <GitFork size={22} aria-hidden />
          {t.title}
        </span>
      </legend>
      <p className="text-ink-soft">{t.lead(steps)}</p>
      <ul className="grid gap-3">
        {options.map((option, i) => (
          <li key={option.id}>
            <button
              type="button"
              data-testid={`branch-${option.id}`}
              aria-label={[t.go(i + 1), t.firstCell(option.cell), option.zone, option.end]
                .filter(Boolean)
                .join('. ')}
              disabled={pending}
              onClick={() => {
                onChoose(option.id);
              }}
              className="grid w-full cursor-pointer grid-cols-[auto_minmax(0,1fr)] items-center gap-x-3 rounded-md border-2 border-ink bg-card p-3 text-left transition duration-(--duration-fast) ease-out is-hover:bg-page is-focus:focus-ring disabled:cursor-default disabled:opacity-50"
            >
              <span
                aria-hidden
                className="grid size-10 place-items-center rounded-full border-2 border-ink bg-me font-display text-lg font-heavy text-on-color"
              >
                {i + 1}
              </span>
              <span className="grid gap-1">
                <span className="flex flex-wrap items-center gap-2">
                  <strong className="font-display">{t.option(i + 1)}</strong>
                  {option.zone ? <Tag>{option.zone}</Tag> : null}
                </span>
                <span className="text-sm text-ink-soft">{t.firstCell(option.cell)}</span>
                <span className="text-sm">{option.end}</span>
              </span>
            </button>
          </li>
        ))}
      </ul>
    </fieldset>
  );
}
