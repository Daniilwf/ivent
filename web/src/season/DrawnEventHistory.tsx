import { useState, type ReactNode } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Summary } from '../ui/Disclosure';
import { DrawnEventCard } from './DrawnEventCard';

/** Only the historical card folds: proof forms and unresolved tasks keep their own state and place. */
export function DrawnEventHistory({
  drawn,
  fresh,
  revealing = false,
  className,
  actions,
  initiallyOpen,
  onShown,
  onSettled,
}: {
  drawn: Schemas['DrawnEventView'];
  fresh: boolean;
  revealing?: boolean;
  className?: string | undefined;
  actions?: ReactNode;
  initiallyOpen: boolean;
  onShown: (sequence: number) => void;
  onSettled?: ((sequence: number) => void) | undefined;
}) {
  const [open, setOpen] = useState(initiallyOpen);
  return (
    <details
      className="grid min-w-0 gap-2"
      open={open}
      onToggle={(event) => {
        setOpen(event.currentTarget.open);
      }}
    >
      <Summary className="font-bold">
        <span className="wrap-anywhere">
          {revealing ? ru.events.title : ru.events.history(drawn.name)}
        </span>
      </Summary>
      <div className={className ?? 'pt-2'}>
        <DrawnEventCard drawn={drawn} fresh={fresh} onShown={onShown} onSettled={onSettled} />
        {actions}
      </div>
    </details>
  );
}
