import { motion, useReducedMotion } from 'motion/react';
import { useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';

type Drawn = Schemas['DrawnEventView'];

// The tone of each kind of event (GLOSSARY «Вид ивента»): only tokens, the text stays ink for contrast
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
 * shows it at once, and with reduced motion it just stands there.
 */
export function DrawnEventCard({ drawn, fresh = false }: { drawn: Drawn; fresh?: boolean }) {
  const t = ru.events;
  const reduce = useReducedMotion() ?? false;
  const [skipped, setSkipped] = useState(false);
  const animated = fresh && !reduce && !skipped;
  return (
    <div className="perspective-distant">
      <motion.article
        data-testid="drawn-event"
        data-drawing={animated ? 'true' : undefined}
        aria-labelledby={`drawn-event-${String(drawn.sequence)}`}
        className="grid min-w-0 gap-2 rounded-lg border-2 border-ink bg-card p-4"
        initial={animated ? { rotateY: -90, y: -24, scale: 0.92, opacity: 0 } : false}
        animate={{ rotateY: 0, y: 0, scale: 1, opacity: 1 }}
        transition={
          animated ? { duration: drawSeconds, ease: [0.2, 0.7, 0.3, 1] } : { duration: 0 }
        }
        onClick={() => {
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
        <h4
          id={`drawn-event-${String(drawn.sequence)}`}
          className="font-display text-lg font-heavy break-words"
        >
          {drawn.name}
        </h4>
        {drawn.description ? <p className="break-words">{drawn.description}</p> : null}
        <p className="text-sm text-ink-soft">{t.played}</p>
      </motion.article>
    </div>
  );
}
