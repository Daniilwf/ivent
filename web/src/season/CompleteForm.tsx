import { useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { difficultyValues } from '../api/schema';
import { ru } from '../i18n/ru';

export type Completion = {
  difficulty: Schemas['Difficulty'];
  estimatedHours?: number;
  hoursSource?: string;
  challengeDone?: boolean;
  review?: { rating: number; text?: string };
};

const ratings = Array.from({ length: 10 }, (_, i) => i + 1);

/**
 * Completing the active run (D-96): difficulty; an hours estimate with its source only when the game has no hours;
 * the challenge claim; an optional review. Fields left unset are not sent.
 */
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
  const [source, setSource] = useState('');
  const [challengeDone, setChallengeDone] = useState(false);
  const [rating, setRating] = useState('');
  const [reviewText, setReviewText] = useState('');
  const [error, setError] = useState<string | null>(null);

  function submit(event: SyntheticEvent) {
    event.preventDefault();
    const completion: Completion = { difficulty };

    if (needsHours) {
      const estimatedHours = Number(hours.replace(',', '.'));
      if (!Number.isFinite(estimatedHours) || estimatedHours <= 0) {
        setError(ru.turn.hoursInvalid);
        return;
      }
      if (source.trim() === '') {
        setError(ru.turn.hoursSourceRequired);
        return;
      }
      completion.estimatedHours = estimatedHours;
      completion.hoursSource = source.trim();
    }

    const text = reviewText.trim();
    if (rating === '' && text !== '') {
      setError(ru.turn.reviewRatingRequired);
      return;
    }

    if (challengeDone) completion.challengeDone = true;
    if (rating !== '') {
      completion.review =
        text === '' ? { rating: Number(rating) } : { rating: Number(rating), text };
    }

    setError(null);
    onComplete(completion);
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
        <>
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
          <label>
            {ru.turn.hoursSource}
            <input
              data-testid="complete-hours-source"
              maxLength={300}
              value={source}
              onChange={(e) => {
                setSource(e.target.value);
              }}
              aria-describedby="hours-source-hint"
            />
            <small id="hours-source-hint">{ru.turn.hoursSourceHint}</small>
          </label>
        </>
      )}
      <label>
        <input
          type="checkbox"
          checked={challengeDone}
          onChange={(e) => {
            setChallengeDone(e.target.checked);
          }}
        />
        {ru.turn.challengeDone}
      </label>
      <label>
        {ru.turn.reviewRating}
        <select
          data-testid="complete-review-rating"
          value={rating}
          onChange={(e) => {
            setRating(e.target.value);
          }}
        >
          <option value="">{ru.turn.reviewNoRating}</option>
          {ratings.map((r) => (
            <option key={r} value={String(r)}>
              {r}
            </option>
          ))}
        </select>
      </label>
      <label>
        {ru.turn.reviewText}
        <textarea
          data-testid="complete-review-text"
          maxLength={2000}
          value={reviewText}
          onChange={(e) => {
            setReviewText(e.target.value);
          }}
        />
      </label>
      {error && <p role="alert">{error}</p>}
      <button data-testid="complete-submit" type="submit" disabled={pending}>
        {ru.turn.complete}
      </button>
    </form>
  );
}
