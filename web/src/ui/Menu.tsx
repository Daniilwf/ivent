import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { useRef, type ReactNode } from 'react';
import { cx } from './cx';

export type MenuItem = {
  label: string;
  icon?: ReactNode;
  onSelect: () => void;
  danger?: boolean;
  testId?: string;
};

/** A short list of actions behind a button: the user's menu in the header */
export function Menu({ trigger, items }: { trigger: ReactNode; items: MenuItem[] }) {
  // An action that opens another screen places the focus there itself: the menu must not take it back
  const chosen = useRef(false);
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>{trigger}</DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          onCloseAutoFocus={(event) => {
            if (chosen.current) event.preventDefault();
            chosen.current = false;
          }}
          sideOffset={8}
          className="z-10 grid min-w-56 gap-1 rounded-lg border-2 border-ink bg-card p-2 shadow-lift"
        >
          {items.map((item) => (
            <DropdownMenu.Item
              key={item.label}
              data-testid={item.testId}
              onSelect={() => {
                chosen.current = true;
                item.onSelect();
              }}
              className={cx(
                'flex min-h-11 cursor-pointer items-center gap-3 rounded-md px-3 font-medium outline-none data-highlighted:bg-muted data-highlighted:outline-2 data-highlighted:outline-ink',
                item.danger ? 'text-danger' : 'text-ink',
              )}
            >
              {item.icon}
              {item.label}
            </DropdownMenu.Item>
          ))}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
