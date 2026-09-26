import { useRef, useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { difficultyValues } from '../api/schema';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Checkbox, ChoiceGroup, Field, Select, TextArea } from '../ui/Field';

export type Completion = {
  difficulty: Schemas['Difficulty'];
  estimatedHours?: number;
  hoursSource?: string;
  challengeDone?: boolean;
  review?: { rating: number; text?: string };
};

const ratings = Array.from({ length: 10 }, (_, i) => i + 1);
const difficulties = difficultyValues.map((d) => ({ value: d, label: ru.difficulty[d] }));

type Problem = { field: 'hours' | 'source' | 'rating'; text: string };
const fieldIds: Record<Problem['field'], string> = {
  hours: 'complete-hours',
  source: 'complete-hours-source',
  rating: 'complete-review-rating',
};

/**
 * Completing the active run (D-96): difficulty as pills in sight; an hours estimate with its source only when the game
 * has no hours; the challenge claim when the season has challenges on (D-96); an optional review, folded. Fields left
 * unset are not sent. Every mistake is told under its field at once, and the focus goes to the first one.
 */
export function CompleteForm({
  needsHours,
  challengesEnabled = false,
  pending,
  onComplete,
}: {
  needsHours: boolean;
  challengesEnabled?: boolean;
  pending: boolean;
  onComplete: (completion: Completion) => void;
}) {
  const [difficulty, setDifficulty] = useState<Schemas['Difficulty']>('normal');
  const [hours, setHours] = useState('');
  const [source, setSource] = useState('');
  const [challengeDone, setChallengeDone] = useState(false);
  const [rating, setRating] = useState('');
  const [reviewText, setReviewText] = useState('');
  const [problems, setProblems] = useState<Problem[]>([]);
  const [reviewOpen, setReviewOpen] = useState(false);
  const form = useRef<HTMLFormElement>(null);

  function submit(event: SyntheticEvent) {
    event.preventDefault();
    const completion: Completion = { difficulty };
    const found: Problem[] = [];

    if (needsHours) {
      const estimatedHours = Number(hours.replace(',', '.'));
      if (hours.trim() === '' || !Number.isFinite(estimatedHours) || estimatedHours <= 0)
        found.push({ field: 'hours', text: ru.turn.hoursInvalid });
      if (source.trim() === '') found.push({ field: 'source', text: ru.turn.hoursSourceRequired });
      completion.estimatedHours = estimatedHours;
      completion.hoursSource = source.trim();
    }

    const text = reviewText.trim();
    if (rating === '' && text !== '')
      found.push({ field: 'rating', text: ru.turn.reviewRatingRequired });

    setProblems(found);
    const first = found[0];
    if (first) {
      // The review folds: a mistake in it opens it; the focus goes to the first field to fix
      if (found.some((f) => f.field === 'rating')) setReviewOpen(true);
      requestAnimationFrame(() => {
        form.current
          ?.querySelector<HTMLElement>(`[data-testid="${fieldIds[first.field]}"]`)
          ?.focus();
      });
      return;
    }

    if (challengeDone) completion.challengeDone = true;
    if (rating !== '') {
      completion.review =
        text === '' ? { rating: Number(rating) } : { rating: Number(rating), text };
    }
    onComplete(completion);
  }

  const errorOf = (field: Problem['field']) => problems.find((p) => p.field === field)?.text;

  return (
    <form
      ref={form}
      onSubmit={submit}
      noValidate
      data-testid="complete-form"
      aria-labelledby="complete-title"
      className="grid min-w-0 gap-4"
    >
      <h3 id="complete-title" className="font-display font-heavy">
        {ru.turn.completeTitle}
      </h3>
      <ChoiceGroup
        data-testid="complete-difficulty"
        label={ru.turn.difficulty}
        hint={ru.turn.difficultyHint}
        options={difficulties}
        value={difficulty}
        onChange={setDifficulty}
      />
      {needsHours && (
        <>
          <Field
            data-testid="complete-hours"
            label={ru.turn.hours}
            hint={ru.turn.hoursHint}
            inputMode="decimal"
            value={hours}
            error={errorOf('hours')}
            onChange={(e) => {
              setHours(e.target.value);
            }}
          />
          <Field
            data-testid="complete-hours-source"
            label={ru.turn.hoursSource}
            hint={ru.turn.hoursSourceHint}
            maxLength={300}
            value={source}
            error={errorOf('source')}
            onChange={(e) => {
              setSource(e.target.value);
            }}
          />
        </>
      )}
      {challengesEnabled && (
        <Checkbox
          label={ru.turn.challengeDone}
          checked={challengeDone}
          onChange={(e) => {
            setChallengeDone(e.target.checked);
          }}
        />
      )}
      <details
        open={reviewOpen}
        onToggle={(e) => {
          setReviewOpen(e.currentTarget.open);
        }}
        className="grid gap-3"
      >
        <summary className="min-h-11 cursor-pointer content-center rounded-md font-bold is-focus:focus-ring">
          {ru.turn.review}
        </summary>
        <div className="grid gap-4 pt-2">
          <Select
            data-testid="complete-review-rating"
            label={ru.turn.reviewRating}
            value={rating}
            error={errorOf('rating')}
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
          </Select>
          <TextArea
            data-testid="complete-review-text"
            label={ru.turn.reviewText}
            maxLength={2000}
            value={reviewText}
            onChange={(e) => {
              setReviewText(e.target.value);
            }}
          />
        </div>
      </details>
      <Button
        variant="main"
        data-testid="complete-submit"
        type="submit"
        loading={pending}
        disabled={pending}
      >
        {ru.turn.complete}
      </Button>
    </form>
  );
}
