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

/** The event I drew last (D-500): its kind and where it came from, its name and its text, already played */
export function DrawnEventCard({ drawn }: { drawn: Drawn }) {
  const t = ru.events;
  return (
    <article
      data-testid="drawn-event"
      aria-labelledby={`drawn-event-${String(drawn.sequence)}`}
      className="grid min-w-0 gap-2 rounded-lg border-2 border-ink bg-card p-4"
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
    </article>
  );
}
