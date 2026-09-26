import { RefreshCw } from 'lucide-react';
import { useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';

type Price = Schemas['RerollPriceView'];

const isPaid = (price: Price | null) =>
  price !== null &&
  (price.payment === 'badEvent' || (price.payment === 'coins' && price.coins > 0));

/** «Реролл» with its price (D-93); a paid reroll asks for confirmation first. */
export function RerollButton({
  price,
  pending,
  onReroll,
}: {
  price: Price | null;
  pending: boolean;
  onReroll: () => void;
}) {
  const [confirming, setConfirming] = useState(false);

  if (confirming && price) {
    return (
      <div
        role="group"
        aria-label={ru.turn.reroll}
        data-testid="reroll-confirm"
        className="grid gap-3 rounded-md bg-muted p-3"
      >
        <p>{ru.turn.rerollConfirm(price.payment, price.coins)}</p>
        <div className="flex flex-wrap gap-3">
          <Button
            variant="main"
            data-testid="reroll-confirm-yes"
            disabled={pending}
            onClick={() => {
              setConfirming(false);
              onReroll();
            }}
          >
            {ru.turn.rerollConfirmYes}
          </Button>
          <Button
            data-testid="reroll-confirm-no"
            onClick={() => {
              setConfirming(false);
            }}
          >
            {ru.turn.rerollConfirmNo}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <Button
      data-testid="reroll"
      icon={<RefreshCw size={18} aria-hidden />}
      disabled={pending}
      onClick={() => {
        if (isPaid(price)) setConfirming(true);
        else onReroll();
      }}
    >
      {price ? ru.turn.rerollFor(price.payment, price.coins) : ru.turn.reroll}
    </Button>
  );
}
