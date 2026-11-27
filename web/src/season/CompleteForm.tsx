import { X } from 'lucide-react';
import { useRef, useState, type ChangeEvent, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { uploadFile } from '../api/files';
import { difficultyValues } from '../api/schema';
import { ru } from '../i18n/ru';
import { CompletionRulesLink } from '../rules/CompletionRulesLink';
import { Button, IconButton } from '../ui/Button';
import { Checkbox, ChoiceGroup, Field, FilePicker, Select, TextArea } from '../ui/Field';
import { Summary } from '../ui/Disclosure';
import { proofPictureTypes, proofUploadError, type ProofShot } from './proofAttachments';

export type Completion = {
  difficulty: Schemas['Difficulty'];
  estimatedHours?: number;
  hoursSource?: string;
  challengeDone?: boolean;
  review?: { rating: number; text?: string };
};

// SeasonEndpoints.MaxHours of the server
const maxHours = 1000;
const ratings = Array.from({ length: 10 }, (_, i) => i + 1);
const difficulties = difficultyValues.map((d) => ({ value: d, label: ru.difficulty[d] }));

type Problem = { field: 'hours' | 'source' | 'rating'; text: string };
const fieldIds: Record<Problem['field'], string> = {
  hours: 'complete-hours',
  source: 'complete-hours-source',
  rating: 'complete-review-rating',
};

/**
 * Completing the active run (D-96): difficulty as pills in sight, no higher than the one named at the start (D-633); an hours estimate with its source only when the game
 * has no hours; the challenge claim when the season has challenges on (D-96); an optional review, folded. Fields left
 * unset are not sent. Every mistake is told under its field at once, and the focus goes to the first one.
 */
export function CompleteForm({
  needsHours,
  challengesEnabled = false,
  dice = null,
  declared = null,
  showHeading = true,
  pending,
  proofDraft,
  onComplete,
}: {
  /** The die of each difficulty under the run's snapshot, and its event */
  dice?: readonly Schemas['DifficultyDieView'][] | null;
  /** The difficulty named at the start: the completion counts no higher (D-633); null — none named */
  declared?: Schemas['Difficulty'] | null;
  needsHours: boolean;
  challengesEnabled?: boolean;
  showHeading?: boolean;
  pending: boolean;
  proofDraft?: { shot: ProofShot | undefined; onChange: (shot: ProofShot | undefined) => void };
  onComplete: (completion: Completion) => void;
}) {
  const [difficulty, setDifficulty] = useState<Schemas['Difficulty']>(declared ?? 'normal');
  // The server counts the lower of the start's and the claimed one: higher pills would promise a die it never throws
  const offered = declared
    ? difficulties.slice(0, difficultyValues.indexOf(declared) + 1)
    : difficulties;
  const hint = declared
    ? `${ru.turn.difficultyHint}. ${ru.turn.difficultyDeclared(ru.difficulty[declared])}.`
    : `${ru.turn.difficultyHint}.`;
  const [hours, setHours] = useState('');
  const [source, setSource] = useState('');
  const [challengeDone, setChallengeDone] = useState(false);
  const [rating, setRating] = useState('');
  const [reviewText, setReviewText] = useState('');
  const [localShot, setLocalShot] = useState<ProofShot>();
  const shot = proofDraft ? proofDraft.shot : localShot;
  const setShot = proofDraft?.onChange ?? setLocalShot;
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [problems, setProblems] = useState<Problem[]>([]);
  const [reviewOpen, setReviewOpen] = useState(shot !== undefined);
  const form = useRef<HTMLFormElement>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const input = event.currentTarget;
    const picked = input.files?.[0];
    input.value = '';
    if (!picked || pending || uploading) return;

    setUploading(true);
    setUploadError(null);
    try {
      const { id, thumbnailUrl } = await uploadFile(picked);
      if (form.current) setShot({ id, thumbnailUrl });
    } catch (error) {
      setUploadError(proofUploadError(error));
      setReviewOpen(true);
    } finally {
      setUploading(false);
    }
  }

  function submit(event: SyntheticEvent) {
    event.preventDefault();
    if (pending || uploading) return;
    const completion: Completion = { difficulty };
    const found: Problem[] = [];

    if (needsHours) {
      const estimatedHours = Number(hours.replace(',', '.'));
      // The server takes more than 0 and at most SeasonEndpoints.MaxHours (K1S-5)
      if (
        hours.trim() === '' ||
        !Number.isFinite(estimatedHours) ||
        estimatedHours <= 0 ||
        estimatedHours > maxHours
      )
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
      aria-labelledby={showHeading ? 'complete-title' : undefined}
      aria-label={showHeading ? undefined : ru.turn.completeTitle}
      className="grid min-w-0 gap-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
        {showHeading ? (
          <h3 id="complete-title" className="font-display font-heavy">
            {ru.turn.completeTitle}
          </h3>
        ) : null}
        <CompletionRulesLink />
      </div>
      <ChoiceGroup
        data-testid="complete-difficulty"
        label={ru.turn.difficulty}
        hint={
          dice && dice.length > 0
            ? `${hint} ${ru.turn.difficultyDice(
                dice.map((d) => ({
                  label: ru.difficulty[d.difficulty],
                  sides: d.sides,
                  grant: d.grantEvent ?? null,
                })),
              )}`
            : hint
        }
        options={offered}
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
        <Summary className="font-bold">{ru.turn.review}</Summary>
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
          <div className="grid gap-2">
            {shot ? (
              <div className="flex items-center gap-2">
                <img
                  src={shot.thumbnailUrl}
                  alt={ru.proof.shotAlt(1)}
                  width={72}
                  height={72}
                  className="size-18 rounded-sm border-2 border-ink object-cover"
                />
                <IconButton
                  label={ru.proof.removeShot(1)}
                  disabled={pending || uploading}
                  onClick={() => {
                    setShot(undefined);
                    setUploadError(null);
                    fileInput.current?.focus();
                  }}
                >
                  <X size={20} aria-hidden />
                </IconButton>
              </div>
            ) : null}
            <FilePicker
              ref={fileInput}
              data-testid="complete-proof-file"
              label={shot ? ru.turn.proofShotReplace : ru.turn.proofShot}
              hint={ru.turn.proofShotHint}
              accept={proofPictureTypes}
              busy={uploading}
              disabled={pending}
              onChange={(event) => {
                void upload(event);
              }}
            />
            <p role="status" className="text-sm text-ink-soft empty:hidden">
              {uploading ? ru.upload.uploading : ''}
            </p>
            {uploadError ? (
              <p role="alert" className="text-sm font-medium text-danger">
                {uploadError}
              </p>
            ) : null}
          </div>
        </div>
      </details>
      <Button
        variant="main"
        data-testid="complete-submit"
        type="submit"
        loading={pending || uploading}
        disabled={pending || uploading}
      >
        {ru.turn.complete}
      </Button>
    </form>
  );
}
