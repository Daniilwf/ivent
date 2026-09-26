import * as AlertDialog from '@radix-ui/react-alert-dialog';
import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import type { ReactNode } from 'react';
import { ru } from '../i18n/ru';
import { Button, IconButton } from './Button';

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
}: {
  trigger?: ReactNode;
  title: string;
  consequences: readonly string[];
  confirm: string;
  onConfirm: () => void;
  busy?: boolean;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
}) {
  return (
    <AlertDialog.Root
      {...(open === undefined ? {} : { open })}
      {...(onOpenChange ? { onOpenChange } : {})}
    >
      {trigger ? <AlertDialog.Trigger asChild>{trigger}</AlertDialog.Trigger> : null}
      <AlertDialog.Portal>
        <AlertDialog.Overlay className={overlay} />
        <AlertDialog.Content className="fixed inset-x-4 top-1/2 z-20 mx-auto grid max-w-110 -translate-y-1/2 gap-4 rounded-lg border-3 border-ink bg-card p-5 shadow-lift">
          <AlertDialog.Title className="font-display text-xl font-heavy text-balance">
            {title}
          </AlertDialog.Title>
          <AlertDialog.Description asChild>
            <ul className="grid list-disc gap-1 pl-5 text-base">
              {consequences.map((c) => (
                <li key={c}>{c}</li>
              ))}
            </ul>
          </AlertDialog.Description>
          <div className="flex flex-wrap justify-end gap-3">
            <AlertDialog.Cancel asChild>
              <Button>{ru.ui.cancel}</Button>
            </AlertDialog.Cancel>
            <Button variant="dangerMain" loading={busy} onClick={onConfirm}>
              {confirm}
            </Button>
          </div>
        </AlertDialog.Content>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  );
}

/** A panel that slides up from the bottom of the phone: the leaderboard on the main screen */
export function BottomSheet({
  trigger,
  title,
  children,
}: {
  trigger: ReactNode;
  title: string;
  children: ReactNode;
}) {
  return (
    <Dialog.Root>
      <Dialog.Trigger asChild>{trigger}</Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className={overlay} />
        <Dialog.Content className="fixed inset-x-0 bottom-0 z-20 grid max-h-4/5 gap-3 overflow-auto rounded-t-lg border-t-3 border-ink bg-card px-4 pt-3 pb-6">
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
