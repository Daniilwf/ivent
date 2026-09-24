import { useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { difficultyValues } from '../api/schema';
import { ru } from '../i18n/ru';

export type Completion = { difficulty: Schemas['Difficulty']; estimatedHours?: number };

/** Completing the active run: difficulty, and an hours estimate only when the game has no hours. */
export function CompleteForm({
  needsHours,
  pending,
  onComplete,
}: {
  needsHours: boolean;
  pending: boolean;
  onComplete: (completion: Completion) => void;
}) {
  const [difficulty, setDifficulty] = useState<Schemas['Difficulty']>('normal');
  const [hours, setHours] = useState('');
  const [error, setError] = useState<string | null>(null);

  function submit(event: SyntheticEvent) {
    event.preventDefault();
    if (!needsHours) {
      onComplete({ difficulty });
      return;
    }
    const estimatedHours = Number(hours.replace(',', '.'));
    if (!Number.isFinite(estimatedHours) || estimatedHours <= 0) {
      setError(ru.turn.hoursInvalid);
      return;
    }
    setError(null);
    onComplete({ difficulty, estimatedHours });
  }

  return (
    <form onSubmit={submit} data-testid="complete-form">
      <label>
        {ru.turn.difficulty}
        <select
          data-testid="complete-difficulty"
          value={difficulty}
          onChange={(e) => {
            setDifficulty(e.target.value as Schemas['Difficulty']);
          }}
        >
          {difficultyValues.map((d) => (
            <option key={d} value={d}>
              {ru.difficulty[d]}
            </option>
          ))}
        </select>
      </label>
      {needsHours && (
        <label>
          {ru.turn.hours}
          <input
            data-testid="complete-hours"
            inputMode="decimal"
            value={hours}
            onChange={(e) => {
              setHours(e.target.value);
            }}
            aria-describedby="hours-hint"
          />
          <small id="hours-hint">{ru.turn.hoursHint}</small>
        </label>
      )}
      {error && <p role="alert">{error}</p>}
      <button data-testid="complete-submit" type="submit" disabled={pending}>
        {ru.turn.complete}
      </button>
    </form>
  );
}
