import type { AnchorHTMLAttributes, MouseEvent } from 'react';
import { cx } from '../ui/cx';
import { navigate } from './router';

/** A link inside the site: a plain `<a>` (a new tab and copying the address work), opened without a reload */
export function Link({
  to,
  className,
  onClick,
  ...rest
}: AnchorHTMLAttributes<HTMLAnchorElement> & { to: string }) {
  return (
    <a
      {...rest}
      href={to}
      className={cx('is-focus:focus-ring', className)}
      onClick={(e: MouseEvent<HTMLAnchorElement>) => {
        onClick?.(e);
        const plain =
          !e.defaultPrevented &&
          e.button === 0 &&
          !e.metaKey &&
          !e.ctrlKey &&
          !e.shiftKey &&
          !e.altKey &&
          !rest.target;
        if (!plain) return;
        e.preventDefault();
        navigate(to);
      }}
    />
  );
}
