import { useReducedMotion, type animate } from 'motion/react';
import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type { Schemas } from '../api/client';
import { loadMotion } from '../board/loadMotion';
import { momentBudget } from '../board/moment';
import { eventDraw } from '../design/motion';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';

type Drawn = Schemas['DrawnEventView'];

// The tone of each kind of event (GLOSSARY «Вид ивента»): only tokens, each text on its soft fill passes AA
const tones: Record<Drawn['kind'], string> = {
  good: 'bg-success-soft text-success',
  bad: 'bg-danger-soft text-danger',
  special: 'bg-info-soft text-info',
  risky: 'bg-warning-soft text-warning',
};

function drawStyle(progress: number) {
  const mix = (from: number, to: number) => from + (to - from) * progress;
  return {
    opacity: mix(eventDraw.initial.opacity, eventDraw.animate.opacity),
    transform:
      progress === 1
        ? 'none'
        : `translateY(${String(mix(eventDraw.initial.y, eventDraw.animate.y))}px) scale(${String(mix(eventDraw.initial.scale, eventDraw.animate.scale))}) rotateY(${String(mix(eventDraw.initial.rotateY, eventDraw.animate.rotateY))}deg)`,
  };
}

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
  onSettled,
}: {
  drawn: Drawn;
  fresh?: boolean;
  waitingForAnswer?: boolean;
  headingId?: string;
  level?: 3 | 4;
  children?: ReactNode;
  onShown?: ((sequence: number) => void) | undefined;
  onSettled?: ((sequence: number) => void) | undefined;
}) {
  const t = ru.events;
  const reduce = useReducedMotion() ?? false;
  const [skipped, setSkipped] = useState(false);
  const [progress, setProgress] = useState(0);
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
  useEffect(() => {
    if (!animated) onSettled?.(drawn.sequence);
  }, [animated, drawn.sequence, onSettled]);
  useEffect(() => {
    if (!animated) return;
    let cancelled = false;
    let controls: ReturnType<typeof animate> | null = null;
    // Downloading the animation also counts against the moment's budget.
    const timer = window.setTimeout(() => {
      setSkipped(true);
    }, momentBudget * 1000);
    const run = async () => {
      const motion = await loadMotion().catch(() => null);
      if (cancelled) return;
      if (!motion) {
        setSkipped(true);
        return;
      }
      // Animate a scalar, not the element: React can reveal the same card synchronously on skip,
      // even before the lazy renderer exists, without a later Motion frame hiding its children again.
      controls = motion.animate(0, 1, {
        ...eventDraw.transition,
        onUpdate: (value) => {
          if (!cancelled) setProgress(value);
        },
        onComplete: () => {
          if (!cancelled) setSkipped(true);
        },
      });
    };
    void run();
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
      controls?.stop();
    };
  }, [animated]);
  const titleId = headingId ?? `drawn-event-${String(drawn.sequence)}`;
  const Heading = level === 3 ? 'h3' : 'h4';
  return (
    <div className="perspective-distant">
      <article
        data-testid="drawn-event"
        data-drawing={animated ? 'true' : undefined}
        aria-labelledby={titleId}
        className="grid min-w-0 gap-1 rounded-lg border-2 border-ink bg-card p-4"
        style={drawStyle(animated ? progress : 1)}
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
      </article>
    </div>
  );
}
