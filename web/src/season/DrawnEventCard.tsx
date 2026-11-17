import { m, useReducedMotion } from 'motion/react';
import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Motion } from '../board/Motion';
import { cx } from '../ui/cx';

type Drawn = Schemas['DrawnEventView'];

// The tone of each kind of event (GLOSSARY «Вид ивента»): only tokens, each text on its soft fill passes AA
const tones: Record<Drawn['kind'], string> = {
  good: 'bg-success-soft text-success',
  bad: 'bg-danger-soft text-danger',
  special: 'bg-info-soft text-info',
  risky: 'bg-warning-soft text-warning',
};

/** How long the card takes to come off the deck and turn its face up: a key moment, well under 3 s (DESIGN.md) */
const drawSeconds = 0.9;

/**
 * The event I drew last (D-500): its kind and where it came from, its name and its text, already played. A card drawn
 * while the page is open (`fresh`) comes off the deck and turns face up — a key moment (DESIGN.md «Движение»); a click
 * shows it at once, and with reduced motion it just stands there. Once it stands, it is an ordinary card.
 */
export function DrawnEventCard({
  drawn,
  fresh = false,
  waitingForAnswer = false,
  headingId,
  level = 4,
  children,
  onShown,
}: {
  drawn: Drawn;
  fresh?: boolean;
  waitingForAnswer?: boolean;
  headingId?: string;
  level?: 3 | 4;
  children?: ReactNode;
  onShown?: ((sequence: number) => void) | undefined;
}) {
  const t = ru.events;
  const reduce = useReducedMotion() ?? false;
  const [skipped, setSkipped] = useState(false);
  // The sequence owns the moment: a live refresh must not restart or cut a draw already in progress.
  const [freshOnMount] = useState(fresh);
  const reported = useRef<number | null>(null);
  useEffect(() => {
    if (freshOnMount && reported.current !== drawn.sequence) {
      reported.current = drawn.sequence;
      onShown?.(drawn.sequence);
    }
  }, [freshOnMount, drawn.sequence, onShown]);
  const animated = freshOnMount && !reduce && !skipped;
  const titleId = headingId ?? `drawn-event-${String(drawn.sequence)}`;
  const Heading = level === 3 ? 'h3' : 'h4';
  return (
    <Motion>
      <div className="perspective-distant">
        <m.article
          data-testid="drawn-event"
          data-drawing={animated ? 'true' : undefined}
          aria-labelledby={titleId}
          className="grid min-w-0 gap-1 rounded-lg border-2 border-ink bg-card p-4"
          initial={animated ? { rotateY: -90, y: -24, scale: 0.92, opacity: 0 } : false}
          animate={{ rotateY: 0, y: 0, scale: 1, opacity: 1 }}
          transition={
            animated ? { duration: drawSeconds, ease: [0.2, 0.7, 0.3, 1] } : { duration: 0 }
          }
          onClick={() => {
            if (animated) setSkipped(true);
          }}
          onAnimationComplete={() => {
            if (animated) setSkipped(true);
          }}
        >
          <p className="flex flex-wrap items-center gap-2 text-sm">
            <span
              data-testid="drawn-event-kind"
              className={cx('rounded-full px-2 py-1 font-bold', tones[drawn.kind])}
            >
              {t.kinds[drawn.kind]}
            </span>
            <span className="text-ink-soft">{t.sources[drawn.source]}</span>
          </p>
          <Heading
            id={titleId}
            tabIndex={headingId ? -1 : undefined}
            className="font-display text-lg font-heavy break-words focus:outline-none"
          >
            {drawn.name}
          </Heading>
          {drawn.description ? <p className="break-words">{drawn.description}</p> : null}
          <p className="text-sm text-ink-soft">{waitingForAnswer ? t.waitingAnswer : t.played}</p>
          {children}
        </m.article>
      </div>
    </Motion>
  );
}
