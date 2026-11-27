import { X } from 'lucide-react';
import {
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ChangeEvent,
  type SyntheticEvent,
} from 'react';
import type { Schemas } from '../api/client';
import { uploadFile, UploadError } from '../api/files';
import { ru } from '../i18n/ru';
import { Button, IconButton } from '../ui/Button';
import { standaloneLink } from '../ui/buttonStyles';
import { cx } from '../ui/cx';
import { Field, FilePicker } from '../ui/Field';
import { Notice } from '../ui/States';
import { parseIrlLink, trimIrlLink } from './irlLink';

export type IrlProofSectionProps = {
  effect: Schemas['ManualEffectView'];
  maxFiles: number;
  pending: boolean;
  enabled: boolean;
  unavailableReason?: string;
  onSubmit: (files: string[], link: string | null) => void;
};

type Draft = {
  files: readonly Schemas['FileLinkView'][];
  link: string;
  source: string | null;
  dirty: boolean;
};

type Submission = { materials: string; source: string | null };

type FocusReturn = { origin: HTMLElement; after: 'upload' | 'submit'; sawPending: boolean };

function returnOwnedFocus(origin: HTMLElement, target: HTMLElement | null | undefined) {
  if (document.activeElement === origin || document.activeElement === document.body)
    target?.focus();
}

const pictureTypes = 'image/png,image/jpeg,image/webp,image/gif';
const acceptedTypes = new Set(pictureTypes.split(','));

function materialsOf(files: readonly Schemas['FileLinkView'][], link: string | null | undefined) {
  return JSON.stringify([files.map((file) => file.id), trimIrlLink(link ?? '')]);
}

function sourceOf(proof: Schemas['IrlProofView'] | null | undefined) {
  return proof
    ? JSON.stringify([proof.submittedAt, proof.status, materialsOf(proof.files, proof.link)])
    : null;
}

function draftOf(proof: Schemas['IrlProofView'] | null | undefined): Draft {
  return {
    files: [...(proof?.files ?? [])],
    link: proof?.link ?? '',
    source: sourceOf(proof),
    dirty: false,
  };
}

/** Each task owns its draft; changing a different task's status never replaces this form. */
export function IrlProofSection(props: IrlProofSectionProps) {
  return <IrlProofForm key={props.effect.id} {...props} />;
}

function IrlProofForm({
  effect,
  maxFiles,
  pending,
  enabled,
  unavailableReason,
  onSubmit,
}: IrlProofSectionProps) {
  const t = ru.irlProof;
  const proof = effect.irlProof;
  const [draft, setDraft] = useState(() => draftOf(proof));
  const [submission, setSubmission] = useState<Submission | null>(null);
  const incomingAt = proof?.submittedAt ?? null;
  const incomingSource = sourceOf(proof);
  if (
    proof &&
    submission &&
    incomingSource !== submission.source &&
    materialsOf(proof.files, proof.link) === submission.materials
  ) {
    // Acknowledging a sent draft must not erase edits made while its response was in flight.
    setSubmission(null);
    if (materialsOf(draft.files, draft.link) === submission.materials) setDraft(draftOf(proof));
  } else if (proof && incomingSource !== draft.source && !draft.dirty) {
    // Another tab may send first, including a different proof with the same clock timestamp.
    setDraft(draftOf(proof));
  }
  const [uploading, setUploading] = useState(false);
  const [uploadProblem, setUploadProblem] = useState<string | null>(null);
  const [formProblem, setFormProblem] = useState<string | null>(null);
  const uploadingNow = useRef(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const form = useRef<HTMLFormElement>(null);
  const status = useRef<HTMLDivElement>(null);
  const focusReturn = useRef<FocusReturn | null>(null);
  const titleId = useId();
  const capProblemId = useId();
  const readOnlyProof = proof?.status === 'pending' || proof?.status === 'approved';
  const open = enabled && !readOnlyProof;
  const busy = pending || uploading;
  const cap = Math.max(0, maxFiles);
  const link = trimIrlLink(draft.link);
  const badLink = link !== '' && parseIrlLink(link) === null;
  const overCap = draft.files.length > cap;
  const canSubmit =
    open && !busy && !badLink && !overCap && (link !== '' || draft.files.length > 0);
  const shownFiles = readOnlyProof ? proof.files : draft.files;
  const shownLink = readOnlyProof ? proof.link : !open ? draft.link : null;
  const shownUrl = shownLink ? parseIrlLink(shownLink) : null;

  useLayoutEffect(() => {
    // Disabling a control can blur it to body. A later real move to another control releases our focus claim.
    const release = (event: FocusEvent) => {
      if (
        focusReturn.current &&
        event.target !== focusReturn.current.origin &&
        event.target !== document.body
      )
        focusReturn.current = null;
    };
    document.addEventListener('focusin', release);
    return () => {
      document.removeEventListener('focusin', release);
    };
  }, []);

  useLayoutEffect(() => {
    const owed = focusReturn.current;
    if (!owed) return;
    if (owed.after === 'upload') {
      if (uploading || pending) return;
      focusReturn.current = null;
      if (open)
        returnOwnedFocus(
          owed.origin,
          fileInput.current ??
            form.current?.querySelector<HTMLInputElement>('[data-testid="irl-proof-link"]'),
        );
    } else if (pending) {
      owed.sawPending = true;
    } else if (readOnlyProof) {
      focusReturn.current = null;
      returnOwnedFocus(owed.origin, status.current);
    } else if (owed.sawPending) {
      focusReturn.current = null;
      if (open && owed.origin.isConnected) returnOwnedFocus(owed.origin, owed.origin);
    }
  }, [uploading, pending, open, readOnlyProof, incomingAt, cap, draft.files.length]);

  function submit(event: SyntheticEvent) {
    event.preventDefault();
    if (!open || pending || uploadingNow.current) return;
    if (badLink || overCap) return;
    if (link === '' && draft.files.length === 0) {
      setFormProblem(t.required);
      return;
    }
    setFormProblem(null);
    const active = document.activeElement;
    focusReturn.current =
      active instanceof HTMLElement && form.current?.contains(active)
        ? { origin: active, after: 'submit', sawPending: false }
        : null;
    setSubmission({ materials: materialsOf(draft.files, link), source: incomingSource });
    onSubmit(
      draft.files.map((file) => file.id),
      link === '' ? null : link,
    );
  }

  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const input = event.currentTarget;
    const picked = input.files?.[0];
    input.value = '';
    if (!picked || !open || pending || uploadingNow.current || draft.files.length >= cap) return;
    setFormProblem(null);
    setUploadProblem(null);
    if (picked.type !== '' && !acceptedTypes.has(picked.type)) {
      setUploadProblem(ru.upload.errors['file.typeInvalid']);
      return;
    }
    focusReturn.current =
      document.activeElement === input
        ? { origin: input, after: 'upload', sawPending: false }
        : null;
    uploadingNow.current = true;
    setUploading(true);
    try {
      const stored = await uploadFile(picked);
      setDraft((current) =>
        current.files.some((file) => file.id === stored.id)
          ? current
          : {
              ...current,
              files: [
                ...current.files,
                { id: stored.id, url: stored.url, thumbnailUrl: stored.thumbnailUrl },
              ],
              dirty: true,
            },
      );
    } catch (error) {
      const code = error instanceof UploadError ? error.code : null;
      setUploadProblem(
        (code && ru.upload.errors[code as keyof typeof ru.upload.errors]) ?? ru.upload.failed,
      );
    } finally {
      uploadingNow.current = false;
      setUploading(false);
    }
  }

  return (
    <section
      className="grid min-w-0 gap-3"
      aria-labelledby={titleId}
      data-testid="irl-proof-task"
      aria-busy={busy || undefined}
    >
      <h4 id={titleId} className="font-display font-heavy wrap-anywhere">
        {effect.name ?? t.title}
      </h4>
      {effect.description ? (
        <p className="max-w-prose wrap-anywhere">{effect.description}</p>
      ) : null}
      {proof ? (
        <div ref={status} tabIndex={-1} data-testid="irl-proof-status" className="grid gap-2">
          <Notice
            tone={
              proof.status === 'rejected'
                ? 'warning'
                : proof.status === 'approved'
                  ? 'success'
                  : 'info'
            }
          >
            {proof.status === 'pending'
              ? t.pending
              : proof.status === 'rejected'
                ? t.rejected
                : ru.proof.status.approved}
          </Notice>
          {proof.comment ? <p className="text-sm wrap-anywhere">{proof.comment}</p> : null}
        </div>
      ) : null}
      {!enabled ? <p className="text-sm text-ink-soft">{unavailableReason ?? t.disabled}</p> : null}
      {shownFiles.length > 0 ? (
        <ul className="flex flex-wrap gap-3" data-testid="irl-proof-photos">
          {shownFiles.map((file, index) => (
            <li key={file.id} className="grid justify-items-center gap-1">
              <a
                href={file.url}
                target="_blank"
                rel="noopener noreferrer"
                className="block rounded-sm is-focus:focus-ring"
              >
                <img
                  src={file.thumbnailUrl}
                  alt={t.photoAlt(index + 1)}
                  width={72}
                  height={72}
                  className="size-18 rounded-sm border-2 border-ink object-cover"
                />
              </a>
              {open ? (
                <IconButton
                  label={t.removePhoto(index + 1)}
                  disabled={busy}
                  onClick={() => {
                    if (busy) return;
                    setDraft((current) => ({
                      ...current,
                      files: current.files.filter((photo) => photo.id !== file.id),
                      dirty: true,
                    }));
                    requestAnimationFrame(() => {
                      const next =
                        fileInput.current ??
                        form.current?.querySelector<HTMLInputElement>(
                          '[data-testid="irl-proof-link"]',
                        );
                      next?.focus();
                    });
                  }}
                >
                  <X size={20} aria-hidden />
                </IconButton>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
      {!open && shownLink ? (
        shownUrl ? (
          <a
            href={trimIrlLink(shownLink)}
            target="_blank"
            rel="noopener noreferrer"
            className={cx(standaloneLink, 'max-w-full justify-self-start wrap-anywhere')}
          >
            {shownUrl.hostname}
          </a>
        ) : (
          <p className="text-sm wrap-anywhere">{shownLink}</p>
        )
      ) : null}
      {open ? (
        <form
          ref={form}
          noValidate
          onSubmit={submit}
          className="grid min-w-0 gap-3"
          data-testid="irl-proof-form"
          aria-describedby={overCap ? capProblemId : undefined}
        >
          <p className="text-sm text-ink-soft">
            {proof?.status === 'rejected' ? t.retryLead : t.lead}
          </p>
          {cap === 0 ? <p className="text-sm text-ink-soft">{t.linkOnly}</p> : null}
          {draft.files.length < cap ? (
            <FilePicker
              ref={fileInput}
              data-testid="irl-proof-file"
              label={t.photo}
              hint={t.photoHint(cap - draft.files.length)}
              accept={pictureTypes}
              busy={uploading}
              disabled={pending}
              onChange={(event) => {
                void upload(event);
              }}
            />
          ) : null}
          <p role="status" className="text-sm text-ink-soft empty:hidden">
            {uploading ? ru.upload.uploading : ''}
          </p>
          {uploadProblem ? <Notice tone="danger">{uploadProblem}</Notice> : null}
          {overCap ? (
            <p id={capProblemId} className="text-sm font-medium text-danger">
              {t.limit(cap)}
            </p>
          ) : null}
          <Field
            label={t.link}
            hint={t.linkHint}
            data-testid="irl-proof-link"
            inputMode="url"
            autoComplete="off"
            spellCheck={false}
            value={draft.link}
            disabled={busy}
            error={badLink ? t.linkInvalid : undefined}
            announce={false}
            onChange={(event) => {
              const value = event.currentTarget.value;
              setDraft((current) => ({ ...current, link: value, dirty: true }));
              setFormProblem(null);
            }}
          />
          {formProblem ? <Notice tone="danger">{formProblem}</Notice> : null}
          <Button
            type="submit"
            aria-label={t.submit}
            loading={pending}
            disabled={!canSubmit}
            className="justify-self-start"
          >
            {t.submit}
          </Button>
        </form>
      ) : null}
    </section>
  );
}
