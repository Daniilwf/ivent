import { RefreshCw } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
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
  // The confirmation replaces the button: the focus goes into it and comes back when it is cancelled
  const question = useRef<HTMLParagraphElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const back = useRef(false);
  useEffect(() => {
    if (confirming) {
      // jsdom has no scrolling
      if (typeof question.current?.scrollIntoView === 'function') {
        question.current.scrollIntoView({ block: 'nearest' });
      }
      question.current?.focus();
    } else if (back.current) {
      back.current = false;
      button.current?.focus();
    }
  }, [confirming]);

  if (confirming && price) {
    return (
      <div
        role="group"
        aria-label={ru.turn.reroll}
        data-testid="reroll-confirm"
        className="grid w-full scroll-mb-28 gap-3 rounded-md bg-muted p-3"
      >
        <p ref={question} tabIndex={-1} className="focus:outline-none">
          {ru.turn.rerollConfirm(price.payment, price.coins)}
        </p>
        <div className="flex flex-wrap gap-3">
          {/* Not the screen's main blue: «Начать» stays the one main action */}
          <Button
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
            variant="link"
            data-testid="reroll-confirm-no"
            onClick={() => {
              back.current = true;
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
      ref={button}
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
