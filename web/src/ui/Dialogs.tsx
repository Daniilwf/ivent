import * as AlertDialog from '@radix-ui/react-alert-dialog';
import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import type { ReactNode, RefObject } from 'react';
import { ru } from '../i18n/ru';
import { Button, IconButton } from './Button';
import { cx } from './cx';

const overlay = 'fixed inset-0 z-20 bg-ink/40';

/** Confirms a dangerous action and shows what it costs: «Дроп: −2d4 очков и клеток и плохой ивент» */
export function ConfirmDanger({
  trigger,
  title,
  consequences,
  confirm,
  onConfirm,
  busy = false,
  open,
  onOpenChange,
  children,
  testId,
}: {
  trigger?: ReactNode;
  title: string;
  consequences: readonly string[];
  confirm: string;
  onConfirm: () => void;
  busy?: boolean;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  /**
   * What goes with the decision besides the consequences: a word before deciding («рано дропать»), or what the action
   * needs besides a yes — the admin's comment for the log, a reason (checked by onConfirm)
   */
  children?: ReactNode;
  /** The window's test id; its buttons get `-yes` and `-no` */
  testId?: string;
}) {
  return (
    <AlertDialog.Root
      {...(open === undefined ? {} : { open })}
      {...(onOpenChange ? { onOpenChange } : {})}
    >
      {trigger ? <AlertDialog.Trigger asChild>{trigger}</AlertDialog.Trigger> : null}
      <AlertDialog.Portal>
        <AlertDialog.Overlay className={overlay} />
        <AlertDialog.Content
          data-testid={testId}
          className="fixed inset-x-4 top-1/2 z-20 mx-auto grid max-w-110 -translate-y-1/2 gap-4 rounded-lg border-3 border-ink bg-card p-5 shadow-lift"
        >
          <AlertDialog.Title className="font-display text-lg font-heavy text-balance wrap-anywhere">
            {title}
          </AlertDialog.Title>
          <AlertDialog.Description asChild>
            <ul className="grid list-disc gap-1 pl-5 text-base">
              {consequences.map((c) => (
                <li key={c}>{c}</li>
              ))}
            </ul>
          </AlertDialog.Description>
          {children}
          <div className="flex flex-wrap justify-end gap-3">
            <AlertDialog.Cancel asChild>
              <Button data-testid={testId && `${testId}-no`}>{ru.ui.cancel}</Button>
            </AlertDialog.Cancel>
            <Button
              variant="dangerMain"
              data-testid={testId && `${testId}-yes`}
              loading={busy}
              onClick={onConfirm}
            >
              {confirm}
            </Button>
          </div>
        </AlertDialog.Content>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  );
}

/**
 * A form in a window over the page, opened by the page: the tech reroll's reason, a new game for the pool. A sheet
 * from the bottom on a phone, a card in the middle on a desktop; closing returns the focus to what opened it
 */
export function FormDialog({
  open,
  onOpenChange,
  title,
  description,
  wide = false,
  children,
  testId,
  returnFocus,
}: {
  /** The button that opened the window: the focus goes back to it when the window closes */
  returnFocus?: RefObject<HTMLElement | null>;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** What the action will do, as a list: the window is its confirmation too */
  description?: readonly string[];
  /** A long form (the pool's new game): a wider card on a desktop */
  wide?: boolean;
  children: ReactNode;
  testId?: string;
}) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className={overlay} />
        <Dialog.Content
          data-testid={testId}
          {...(description ? {} : { 'aria-describedby': undefined })}
          onCloseAutoFocus={(e) => {
            if (!returnFocus?.current) return;
            e.preventDefault();
            returnFocus.current.focus();
          }}
          className={cx(
            'fixed inset-x-0 bottom-0 z-20 grid max-h-11/12 gap-4 overflow-auto rounded-t-lg border-t-3 border-ink bg-card px-4 pt-3 pb-6',
            'desk:inset-x-4 desk:top-1/2 desk:bottom-auto desk:mx-auto desk:-translate-y-1/2 desk:rounded-lg desk:border-3 desk:p-6 desk:shadow-lift',
            wide ? 'desk:max-w-140' : 'desk:max-w-110',
          )}
        >
          <span
            className="h-1 w-11 justify-self-center rounded-full bg-ink desk:hidden"
            aria-hidden
          />
          <div className="flex items-start justify-between gap-3">
            <Dialog.Title className="font-display text-lg font-heavy text-balance wrap-anywhere">
              {title}
            </Dialog.Title>
            <Dialog.Close asChild>
              <IconButton label={ru.ui.close}>
                <X size={20} />
              </IconButton>
            </Dialog.Close>
          </div>
          {description ? (
            <Dialog.Description asChild>
              <ul className="grid list-disc gap-1 pl-5 text-base">
                {description.map((c) => (
                  <li key={c}>{c}</li>
                ))}
              </ul>
            </Dialog.Description>
          ) : null}
          {children}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

/** A panel that slides up from the bottom of the phone: the leaderboard on the main screen */
export function BottomSheet({
  trigger,
  title,
  children,
  open,
  onOpenChange,
  returnFocus,
}: {
  trigger: ReactNode;
  title: string;
  children: ReactNode;
  /** Held by the page when a choice inside closes the sheet: the admin's list of sections */
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  /** Whether closing gives the focus back to the trigger; a choice that opened a new page places it there itself */
  returnFocus?: () => boolean;
}) {
  return (
    <Dialog.Root
      {...(open === undefined ? {} : { open })}
      {...(onOpenChange ? { onOpenChange } : {})}
    >
      <Dialog.Trigger asChild>{trigger}</Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className={overlay} />
        <Dialog.Content
          onCloseAutoFocus={(event) => {
            if (returnFocus && !returnFocus()) event.preventDefault();
          }}
          className="fixed inset-x-0 bottom-0 z-20 grid max-h-4/5 gap-3 overflow-auto rounded-t-lg border-t-3 border-ink bg-card px-4 pt-3 pb-6"
        >
          <span className="h-1 w-11 justify-self-center rounded-full bg-ink" aria-hidden />
          <div className="flex items-center justify-between gap-3">
            <Dialog.Title className="font-display text-lg font-heavy">{title}</Dialog.Title>
            <Dialog.Close asChild>
              <IconButton label={ru.ui.close}>
                <X size={20} />
              </IconButton>
            </Dialog.Close>
          </div>
          <Dialog.Description className="sr-only">{title}</Dialog.Description>
          {children}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
