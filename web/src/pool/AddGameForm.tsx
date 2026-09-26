import { ImagePlus, X } from 'lucide-react';
import { useEffect, useRef, useState, type SyntheticEvent } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { UploadError, uploadFile } from '../api/files';
import { Cover } from '../board/GameCards';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Checkbox, Field, TextArea } from '../ui/Field';
import { Notice } from '../ui/States';
import type { PoolGame } from './poolFilter';

const t = ru.pool.form;

// The pool's card limits (the server checks the same: PoolRules)
const maxTitle = 200;
const maxNote = 1000;
const minHours = 0.5;
const maxHours = 1000;
const minYear = 1950;
const maxYear = 2100;

/** How long the title rests before the pool is asked for alike ones */
export const similarDelayMs = 400;

type Similar = Schemas['SimilarGameView'];

type Errors = Partial<
  Record<'title' | 'tags' | 'hours' | 'year' | 'note' | 'cover', string | undefined>
>;

/** «12,5» and «12.5» are the same half hours; empty is none; anything else is invalid (NaN) */
function parseNumber(text: string): number | null {
  const trimmed = text.trim().replace(',', '.');
  if (!trimmed) return null;
  return /^\d+(\.\d+)?$/.test(trimmed) ? Number(trimmed) : Number.NaN;
}

function check(
  title: string,
  tags: string[],
  hours: number | null,
  year: number | null,
  note: string,
) {
  const errors: Errors = {};
  if (!title.trim()) errors.title = t.nameRequired;
  else if (title.trim().length > maxTitle) errors.title = t.nameTooLong;
  if (tags.length === 0) errors.tags = t.categoriesRequired;
  if (
    hours !== null &&
    (Number.isNaN(hours) || hours < minHours || hours > maxHours || !Number.isInteger(hours * 2))
  )
    errors.hours = t.hoursInvalid;
  if (
    year !== null &&
    (Number.isNaN(year) || !Number.isInteger(year) || year < minYear || year > maxYear)
  )
    errors.year = t.yearInvalid;
  if (note.trim().length > maxNote) errors.note = t.noteTooLong;
  return errors;
}

/**
 * A player adds a game to the pool (SPEC «Пул игр», «Дубли»): the title, its categories, the hours if known, a note, a
 * cover of their own. While the title is typed the pool is asked for alike ones: the same title cannot be added, an alike
 * one needs «Всё равно добавить».
 */
export function AddGameForm({
  categories,
  onAdded,
  onSignedOut,
}: {
  categories: Schemas['CategoryView'][];
  onAdded: (game: PoolGame) => void;
  onSignedOut: () => void;
}) {
  const [title, setTitle] = useState('');
  const [tags, setTags] = useState<string[]>([]);
  const [hours, setHours] = useState('');
  const [year, setYear] = useState('');
  const [note, setNote] = useState('');
  const [coop, setCoop] = useState(false);
  const [cover, setCover] = useState<Schemas['StoredFileView'] | null>(null);
  const [uploading, setUploading] = useState(false);
  const [similar, setSimilar] = useState<{ title: string; games: Similar[] } | null>(null);
  const [errors, setErrors] = useState<Errors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // One command id per game: a retry of the same submit is the same command
  const commandId = useRef(crypto.randomUUID());
  const fileInput = useRef<HTMLInputElement>(null);

  // The pool is asked about the title once the typing rests; an answer for an older title is dropped
  useEffect(() => {
    const asked = title.trim();
    // An answer is kept with its title: an empty or changed title simply has none
    if (!asked || asked.length > maxTitle) return;
    let current = true;
    const timer = setTimeout(() => {
      void api
        .GET('/api/pool/similar', { params: { query: { title: asked } } })
        .then(({ data }) => {
          // Without an answer the check says nothing: the server still refuses the same title
          if (current) setSimilar({ title: asked, games: data ?? [] });
        })
        .catch(() => {
          if (current) setSimilar({ title: asked, games: [] });
        });
    }, similarDelayMs);
    return () => {
      current = false;
      clearTimeout(timer);
    };
  }, [title]);

  const known = similar?.title === title.trim() ? similar.games : [];
  const checking =
    title.trim().length > 0 && title.trim().length <= maxTitle && similar?.title !== title.trim();
  const same = known.find((g) => g.same);
  const alike = known.filter((g) => !g.same);

  async function pickCover(file: File) {
    setUploading(true);
    setErrors((e) => ({ ...e, cover: undefined }));
    try {
      setCover(await uploadFile(file));
    } catch (error) {
      const code = error instanceof UploadError ? error.code : null;
      setErrors((e) => ({
        ...e,
        cover:
          (code && ru.upload.errors[code as keyof typeof ru.upload.errors]) ?? ru.upload.failed,
      }));
    } finally {
      setUploading(false);
    }
  }

  async function submit(event: SyntheticEvent) {
    event.preventDefault();
    const found = check(title, tags, parseNumber(hours), parseNumber(year), note);
    if (same) found.title = t.same(same.title);
    setErrors(found);
    setFailure(null);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      const { data, error, response } = await api.POST('/api/pool', {
        body: {
          commandId: commandId.current,
          title: title.trim(),
          tags,
          hours: parseNumber(hours),
          year: parseNumber(year),
          note: note.trim() || null,
          isCoop: coop,
          coverFileId: cover?.id ?? null,
          // The alike titles were on the screen next to the button that said «Всё равно добавить»
          force: alike.length > 0,
        },
      });
      if (response.status === 401) {
        onSignedOut();
        return;
      }
      if (data) {
        onAdded(data);
        return;
      }
      // A refused command's id is spent: the next try is a new command
      commandId.current = crypto.randomUUID();
      const code = rejectionCode(error);
      if (code === 'pool.duplicate') setErrors({ title: t.same(title.trim()) });
      else if (code === 'pool.similar') {
        // Someone added an alike title meanwhile: show it, the next press confirms
        const { data: fresh } = await api.GET('/api/pool/similar', {
          params: { query: { title: title.trim() } },
        });
        if (fresh) setSimilar({ title: title.trim(), games: fresh });
        setFailure(ru.rejection[code]);
      } else if (response.status === 429) setFailure(t.tooOften);
      else setFailure((code && ru.rejection[code]) ?? t.failed);
    } catch {
      setFailure(t.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="grid gap-4" onSubmit={(e) => void submit(e)} noValidate data-testid="add-game">
      <Field
        label={t.name}
        hint={t.nameHint}
        value={title}
        maxLength={maxTitle + 20}
        autoComplete="off"
        error={errors.title}
        onChange={(e) => {
          setTitle(e.target.value);
          setErrors((x) => ({ ...x, title: undefined }));
        }}
      />
      <p className="text-sm text-ink-soft" aria-live="polite" data-testid="similar-checking">
        {checking ? t.checking : null}
      </p>
      {same ? (
        <Notice tone="danger">{t.same(same.title)}</Notice>
      ) : alike.length > 0 ? (
        <div className="grid gap-2" data-testid="similar-games">
          <Notice tone="warning">
            <span className="grid gap-1">
              <strong>{t.similarTitle}</strong>
              <span>{t.similarText}</span>
            </span>
          </Notice>
          <ul className="grid list-disc gap-1 pl-8">
            {alike.map((game) => (
              <li key={game.id} className="wrap-anywhere">
                {game.title}
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      <fieldset className="grid gap-1" aria-describedby="add-game-tags-hint">
        <legend className="mb-1 text-sm font-bold">{t.categories}</legend>
        <span id="add-game-tags-hint" className="text-sm text-ink-soft">
          {categories.length === 0 ? t.noCategories : t.categoriesHint}
        </span>
        <span className="grid grid-cols-2 gap-x-3">
          {categories.map((category) => (
            <Checkbox
              key={category.name}
              label={category.name}
              checked={tags.includes(category.name)}
              onChange={(e) => {
                const on = e.target.checked;
                setTags((list) =>
                  on ? [...list, category.name] : list.filter((x) => x !== category.name),
                );
                setErrors((x) => ({ ...x, tags: undefined }));
              }}
            />
          ))}
        </span>
        {errors.tags ? (
          <span role="alert" className="text-sm font-medium text-danger">
            {errors.tags}
          </span>
        ) : null}
      </fieldset>

      <div className="grid gap-4 desk:grid-cols-2">
        <Field
          label={t.hours}
          hint={t.hoursHint}
          inputMode="decimal"
          value={hours}
          error={errors.hours}
          onChange={(e) => {
            setHours(e.target.value);
          }}
        />
        <Field
          label={t.year}
          inputMode="numeric"
          value={year}
          error={errors.year}
          onChange={(e) => {
            setYear(e.target.value);
          }}
        />
      </div>

      <TextArea
        label={t.note}
        hint={t.noteHint}
        rows={3}
        value={note}
        error={errors.note}
        onChange={(e) => {
          setNote(e.target.value);
        }}
      />
      <Checkbox
        label={t.coop}
        checked={coop}
        onChange={(e) => {
          setCoop(e.target.checked);
        }}
      />

      <div className="grid gap-2">
        <span className="text-sm font-bold">{t.cover}</span>
        <div className="flex flex-wrap items-center gap-3">
          {cover ? <Cover game={{ title, cover: cover.thumbnailUrl }} width={48} /> : null}
          <input
            ref={fileInput}
            type="file"
            accept="image/jpeg,image/png,image/webp,image/gif"
            className="sr-only"
            tabIndex={-1}
            aria-hidden
            data-testid="cover-file"
            onChange={(e) => {
              const file = e.target.files?.[0];
              e.target.value = '';
              if (file) void pickCover(file);
            }}
          />
          <Button
            icon={<ImagePlus size={20} aria-hidden />}
            loading={uploading}
            aria-describedby="add-game-cover-hint"
            onClick={() => fileInput.current?.click()}
          >
            {cover ? t.coverReplace : t.coverPick}
          </Button>
          {cover ? (
            <Button
              variant="link"
              icon={<X size={18} aria-hidden />}
              onClick={() => {
                setCover(null);
              }}
            >
              {t.coverRemove}
            </Button>
          ) : null}
        </div>
        <span id="add-game-cover-hint" className="text-sm text-ink-soft">
          {t.coverHint}
        </span>
        {errors.cover ? (
          <span role="alert" className="text-sm font-medium text-danger">
            {errors.cover}
          </span>
        ) : null}
      </div>

      {failure ? <Notice tone="danger">{failure}</Notice> : null}
      <Button
        type="submit"
        variant="main"
        loading={busy}
        disabled={Boolean(same) || uploading}
        className="justify-self-stretch desk:justify-self-end"
      >
        {alike.length > 0 && !same ? t.submitAnyway : t.submit}
      </Button>
    </form>
  );
}
