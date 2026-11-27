import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { cloneElement, useRef, type ReactElement, type ReactNode } from 'react';
import { cx } from './cx';

export type MenuItem = {
  label: string;
  icon?: ReactNode;
  danger?: boolean;
  testId?: string;
  /** A count after the label (the admin's proofs to check); `ariaLabel` then says it in words */
  badge?: ReactNode;
  ariaLabel?: string;
} & (
  | { onSelect: () => void; link?: never }
  | { onSelect?: never; link: ReactElement<{ children?: ReactNode }> }
);

/** A short list of actions or links behind a button */
export function Menu({ trigger, items }: { trigger: ReactNode; items: MenuItem[] }) {
  // An action that opens another screen places the focus there itself: the menu must not take it back
  const chosen = useRef(false);
  const button = useRef<HTMLButtonElement>(null);
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild ref={button}>
        {trigger}
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            if (!chosen.current) button.current?.focus({ preventScroll: true });
            chosen.current = false;
          }}
          sideOffset={8}
          className="z-10 grid min-w-56 gap-1 rounded-lg border-2 border-ink bg-card p-2 shadow-lift"
        >
          {items.map((item) => {
            const content = (
              <>
                {item.icon}
                {item.badge ? <span className="mr-auto">{item.label}</span> : item.label}
                {item.badge}
              </>
            );
            return (
              <DropdownMenu.Item
                key={item.label}
                asChild={item.link !== undefined}
                data-testid={item.testId}
                aria-label={item.ariaLabel}
                onSelect={() => {
                  if (item.onSelect) {
                    chosen.current = true;
                    item.onSelect();
                  }
                }}
                className={cx(
                  'flex min-h-11 cursor-pointer items-center gap-3 rounded-md px-3 font-medium outline-none data-highlighted:bg-muted data-highlighted:outline-2 data-highlighted:outline-ink',
                  item.danger ? 'text-danger' : 'text-ink',
                )}
              >
                {item.link
                  ? cloneElement(item.link, {
                      children: content,
                    })
                  : content}
              </DropdownMenu.Item>
            );
          })}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
