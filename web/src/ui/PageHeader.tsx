import type { ComponentPropsWithRef } from 'react';
import { cx } from './cx';

type PageHeaderProps = ComponentPropsWithRef<'h1'> & {
  /** The sticky admin header uses one smaller step on a phone. */
  compact?: boolean;
};

export function PageHeader({ className, compact = false, ...props }: PageHeaderProps) {
  return (
    <h1
      {...props}
      className={cx(
        'font-display font-heavy',
        compact ? 'text-xl desk:text-2xl' : 'text-2xl',
        className,
      )}
    />
  );
}
