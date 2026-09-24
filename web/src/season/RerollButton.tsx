import { useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

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
      <div role="group" data-testid="reroll-confirm">
        <p>{ru.turn.rerollConfirm(price.payment, price.coins)}</p>
        <button
          data-testid="reroll-confirm-yes"
          disabled={pending}
          onClick={() => {
            setConfirming(false);
            onReroll();
          }}
        >
          {ru.turn.rerollConfirmYes}
        </button>
        <button
          data-testid="reroll-confirm-no"
          onClick={() => {
            setConfirming(false);
          }}
        >
          {ru.turn.rerollConfirmNo}
        </button>
      </div>
    );
  }

  return (
    <button
      data-testid="reroll"
      disabled={pending}
      onClick={() => {
        if (isPaid(price)) setConfirming(true);
        else onReroll();
      }}
    >
      {price ? ru.turn.rerollFor(price.payment, price.coins) : ru.turn.reroll}
    </button>
  );
}
