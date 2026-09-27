import { ImagePlus, X } from 'lucide-react';
import { useEffect, useId, useRef, useState, type SyntheticEvent } from 'react';
import { newCommandId } from '../api/commands';
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
  if (!title.trim()) errors.title = ru.ui.nameRequired;
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

type CoverFile = { id: string; thumbnailUrl: string };

/**
 * The pool's game card as a form, one for everyone (D-202): a player or the admin adds a game (SPEC «Пул игр», «Дубли»),
 * the admin changes one (`game`). The title, its categories, the hours if known, the year, a note, co-op, a cover. While
 * the title is typed the pool is asked for alike ones: the same title cannot be saved, an alike one needs «Всё равно
 * добавить». The same limits as the server's (PoolRules) for both.
 */
export function GameForm({
  categories,
  game,
  onSaved,
  onSignedOut,
}: {
  categories: Schemas['CategoryView'][];
  /** The game to change (the admin); none — a new game */
  game?: PoolGame;
  onSaved: (game: PoolGame) => void;
  onSignedOut: () => void;
}) {
  const [title, setTitle] = useState(game?.title ?? '');
  const [tags, setTags] = useState<string[]>(game?.tags ?? []);
  const [hours, setHours] = useState(game?.hours == null ? '' : String(game.hours));
  const [year, setYear] = useState(game?.year == null ? '' : String(game.year));
  const [note, setNote] = useState(game?.note ?? '');
  const [coop, setCoop] = useState(game?.isCoop ?? false);
  const [cover, setCover] = useState<CoverFile | null>(game?.cover ?? null);
  // The game's own categories stay choosable even when the wheel no longer has them
  const choices = [
    ...categories.map((c) => c.name),
    ...(game?.tags ?? []).filter((tag) => !categories.some((c) => c.name === tag)),
  ];
  const [uploading, setUploading] = useState(false);
  const [similar, setSimilar] = useState<{ title: string; games: Similar[] } | null>(null);
  const [errors, setErrors] = useState<Errors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  // Two forms on one page (the styleguide) keep their hints apart
  const tagsHint = useId();
  const coverHint = useId();

  // The pool is asked about the title once the typing rests; an answer for an older title is dropped
  useEffect(() => {
    const asked = title.trim();
    // An answer is kept with its title: an empty or changed title simply has none; a game's own title is not asked
    if (!asked || asked.length > maxTitle || asked === game?.title) return;
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
  }, [title, game?.title]);

  const own = title.trim() === game?.title;
  const known =
    !own && similar?.title === title.trim() ? similar.games.filter((g) => g.id !== game?.id) : [];
  const checking =
    !own &&
    title.trim().length > 0 &&
    title.trim().length <= maxTitle &&
    similar?.title !== title.trim();
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
      const card = {
        commandId: newCommandId(),
        title: title.trim(),
        tags,
        hours: parseNumber(hours),
        year: parseNumber(year),
        note: note.trim() || null,
        isCoop: coop,
        coverFileId: cover?.id ?? null,
        // The alike titles were on the screen next to the button that said «Всё равно добавить»
        force: alike.length > 0,
      };
      // A change replaces the card whole: what the form does not show is sent as it is
      const { data, error, response } = game
        ? await api.PUT('/api/admin/pool/{gameId}', {
            params: { path: { gameId: game.id } },
            body: {
              ...card,
              steamAppId: game.steamAppId,
              completionCondition: game.completionCondition,
            },
          })
        : await api.POST('/api/pool', { body: card });
      if (response.status === 401) {
        onSignedOut();
        return;
      }
      if (data) {
        onSaved(data);
        return;
      }
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
    <form
      className="grid gap-4"
      onSubmit={(e) => void submit(e)}
      noValidate
      data-testid={game ? 'game-form' : 'add-game'}
      aria-label={game ? ru.admin.pool.editTitle(game.title) : undefined}
    >
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

      <fieldset className="grid gap-1" aria-describedby={tagsHint}>
        <legend className="mb-1 text-sm font-bold">{t.categories}</legend>
        <span id={tagsHint} className="text-sm text-ink-soft">
          {choices.length === 0 ? t.noCategories : t.categoriesHint}
        </span>
        <span className="grid grid-cols-2 gap-x-3">
          {choices.map((name) => (
            <Checkbox
              key={name}
              label={name}
              checked={tags.includes(name)}
              onChange={(e) => {
                const on = e.target.checked;
                setTags((list) => (on ? [...list, name] : list.filter((x) => x !== name)));
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
            aria-describedby={coverHint}
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
        <span id={coverHint} className="text-sm text-ink-soft">
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
        data-testid={game ? 'game-save' : undefined}
      >
        {alike.length > 0 && !same ? t.submitAnyway : game ? ru.admin.save : t.submit}
      </Button>
    </form>
  );
}
