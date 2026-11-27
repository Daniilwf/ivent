import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Check } from 'lucide-react';
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

/** One of a few values picked right in the menu (the theme): the menu stays open, so the change is seen at once */
export type MenuChoice = {
  kind: 'choice';
  label: string;
  value: string;
  options: { value: string; label: string; icon?: ReactNode; testId?: string }[];
  onChange: (value: string) => void;
};

const row =
  'flex min-h-11 cursor-pointer items-center gap-3 rounded-md px-3 font-medium outline-none data-highlighted:bg-muted data-highlighted:outline-2 data-highlighted:outline-ink data-highlighted:focus-visible:outline-solid';

/** A short list of actions, links and choices behind a button */
export function Menu({ trigger, items }: { trigger: ReactNode; items: (MenuItem | MenuChoice)[] }) {
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
          className="z-10 grid max-h-(--radix-dropdown-menu-content-available-height) min-w-56 gap-1 overflow-y-auto overscroll-contain rounded-lg border-2 border-ink bg-card p-2 shadow-lift"
        >
          {items.map((item) => {
            if ('kind' in item) {
              return (
                <DropdownMenu.Group
                  key={item.label}
                  className="grid gap-1 border-y-2 border-muted py-1"
                >
                  <DropdownMenu.Label className="px-3 pt-1 text-sm text-ink-soft">
                    {item.label}
                  </DropdownMenu.Label>
                  <DropdownMenu.RadioGroup
                    aria-label={item.label}
                    value={item.value}
                    onValueChange={item.onChange}
                  >
                    {item.options.map((option) => (
                      <DropdownMenu.RadioItem
                        key={option.value}
                        value={option.value}
                        data-testid={option.testId}
                        onSelect={(event) => {
                          event.preventDefault();
                        }}
                        className={cx(row, 'text-ink data-highlighted:outline-solid')}
                      >
                        {option.icon}
                        <span className="mr-auto">{option.label}</span>
                        <DropdownMenu.ItemIndicator>
                          <Check size={18} aria-hidden />
                        </DropdownMenu.ItemIndicator>
                      </DropdownMenu.RadioItem>
                    ))}
                  </DropdownMenu.RadioGroup>
                </DropdownMenu.Group>
              );
            }
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
                className={cx(row, item.danger ? 'text-danger' : 'text-ink')}
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
