import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

/** Other players who dropped or tech-rerolled an offered game this season (SPEC «Статусы игры в сезоне»). */
export function GameMarks({ marks }: { marks: Schemas['GameMarkView'][] }) {
  if (marks.length === 0) return null;
  return (
    <span data-testid="game-marks">
      {marks.map((mark, i) => (
        <small key={i}> {ru.turn.gameMark(mark.playerName, mark.kind)}</small>
      ))}
    </span>
  );
}
