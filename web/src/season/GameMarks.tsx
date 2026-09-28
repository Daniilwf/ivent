import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

/** Other players who dropped or tech-rerolled an offered game this season (SPEC «Статусы игры в сезоне»). */
export function GameMarks({ marks }: { marks: Schemas['GameMarkView'][] }) {
  if (marks.length === 0) return null;
  return (
    // Spans, not a list: the marks also sit inside a choice card, which is a button
    <span data-testid="game-marks" className="grid gap-1 text-sm text-ink-soft">
      {marks.map((mark, i) => (
        <span key={i}>{ru.turn.gameMark(mark.playerName, mark.kind)}</span>
      ))}
    </span>
  );
}
