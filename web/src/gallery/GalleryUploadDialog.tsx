import { useEffect, useId, useRef, useState, type SubmitEvent, type RefObject } from 'react';
import { newCommandId } from '../api/commands';
import { UploadError } from '../api/files';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { ChoiceGroup, FilePicker, Select, TextArea } from '../ui/Field';
import { Skeleton } from '../ui/Progress';
import { Notice } from '../ui/States';
import {
  listProofCandidates,
  publishMoment,
  publishProofMoment,
  type GalleryMoment,
  type GalleryProofPhoto,
} from './galleryApi';

const t = ru.gallery;
const maxCaptionLength = 500;
const pictureSources = [
  { value: 'device', label: t.fromDevice },
  { value: 'proof', label: t.fromProof },
] as const;

type PictureSource = (typeof pictureSources)[number]['value'];
type ProofPhotos =
  | { kind: 'loading' }
  | { kind: 'failed'; message: string }
  | { kind: 'ready'; items: GalleryProofPhoto[] };

function photoKey({ runId, fileId }: GalleryProofPhoto) {
  return `${runId}:${fileId}`;
}

function uploadIssue(code: string | null) {
  if (code === 'gallery.dailyLimit') return t.dailyLimit;
  if (code === 'gallery.proofUnavailable') return t.proofPhotoUnavailable;
  if (
    code === 'gallery.notParticipant' ||
    code === 'feature.disabled' ||
    code === 'season.closed' ||
    code === 'season.notActive'
  )
    return t.publishUnavailable;
  const messages = ru.upload.errors;
  return code && Object.hasOwn(messages, code)
    ? messages[code as keyof typeof messages]
    : t.publishFailed;
}

export function GalleryUploadDialog({
  seasonId,
  close,
  onPublished,
  returnFocus,
}: {
  seasonId: string;
  close: () => void;
  onPublished: (moment: GalleryMoment) => void;
  returnFocus: RefObject<HTMLButtonElement | null>;
}) {
  const formId = useId();
  const fileErrorId = useId();
  const picker = useRef<HTMLInputElement>(null);
  const photoPicker = useRef<HTMLSelectElement>(null);
  const captionField = useRef<HTMLTextAreaElement>(null);
  const [caption, setCaption] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [source, setSource] = useState<PictureSource>('device');
  const [proofs, setProofs] = useState<ProofPhotos>({ kind: 'loading' });
  const [selectedPhoto, setSelectedPhoto] = useState('');
  const [proofAttempt, setProofAttempt] = useState(0);
  const [fieldError, setFieldError] = useState<'file' | 'proof' | 'caption' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const inFlight = useRef<AbortController | null>(null);
  const retry = useRef<{ commandId: string; itemId: string } | null>(null);
  useEffect(() => () => inFlight.current?.abort(), []);

  useEffect(() => {
    if (source !== 'proof') return;
    const controller = new AbortController();
    void listProofCandidates(seasonId, controller.signal)
      .then((items) => {
        if (!controller.signal.aborted) setProofs({ kind: 'ready', items });
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return;
        const code = failure instanceof UploadError ? failure.code : null;
        setProofs({
          kind: 'failed',
          message:
            code === 'gallery.dailyLimit'
              ? t.dailyLimit
              : code === 'file.tooOften'
                ? t.tooOften
                : t.proofPhotosFailed,
        });
      });
    return () => {
      controller.abort();
    };
  }, [source, proofAttempt, seasonId]);

  const candidate =
    proofs.kind === 'ready'
      ? proofs.items.find((photo) => photoKey(photo) === selectedPhoto)
      : null;
  const titleCounts = new Map<string, number>();
  if (proofs.kind === 'ready') {
    for (const { title } of proofs.items) titleCounts.set(title, (titleCounts.get(title) ?? 0) + 1);
  }
  const titleNumbers = new Map<string, number>();

  function changeSource(next: PictureSource) {
    setSource(next);
    setFile(null);
    setSelectedPhoto('');
    if (next === 'proof') setProofs({ kind: 'loading' });
    retry.current = null;
    setFieldError(null);
    setError(null);
  }

  async function submit(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    if (inFlight.current) return;
    if (source === 'device' && !file) {
      setFieldError('file');
      picker.current?.focus();
      return;
    }
    if (source === 'proof' && !candidate) {
      setFieldError('proof');
      photoPicker.current?.focus();
      return;
    }
    const trimmed = caption.trim();
    if (!trimmed || trimmed.length > maxCaptionLength) {
      setFieldError('caption');
      captionField.current?.focus();
      return;
    }
    const controller = new AbortController();
    inFlight.current = controller;
    setFieldError(null);
    setError(null);
    setBusy(true);
    const ids = retry.current ?? { commandId: newCommandId(), itemId: newCommandId() };
    // Until the payload changes, a network error or an intermediate limit can hide an accepted publication.
    retry.current = ids;
    try {
      const moment = candidate
        ? await publishProofMoment(seasonId, ids, candidate, trimmed, controller.signal)
        : file
          ? await publishMoment(seasonId, ids, file, trimmed, controller.signal)
          : null;
      if (moment && !controller.signal.aborted) onPublished(moment);
    } catch (failure) {
      if (controller.signal.aborted) return;
      if (failure instanceof UploadError) {
        if (
          failure.code === 'gallery.captionRequired' ||
          failure.code === 'gallery.captionTooLong'
        ) {
          setFieldError('caption');
          captionField.current?.focus();
        } else {
          setError(uploadIssue(failure.code));
        }
      } else {
        // The server may have accepted a request without getting its reply back to this browser.
        setError(t.publishFailed);
      }
    } finally {
      inFlight.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  return (
    <FormDialog
      open
      title={t.add}
      returnFocus={returnFocus}
      onOpenChange={(open) => {
        if (!open && !inFlight.current) close();
      }}
      footer={
        <>
          {error ? <Notice tone="danger">{error}</Notice> : null}
          <Button
            type="submit"
            form={formId}
            variant="main"
            loading={busy}
            disabled={source === 'proof' && (proofs.kind !== 'ready' || !proofs.items.length)}
          >
            {t.publish}
          </Button>
        </>
      }
    >
      <form id={formId} className="grid gap-4" noValidate onSubmit={(event) => void submit(event)}>
        <ChoiceGroup
          label={t.pictureSource}
          options={pictureSources}
          value={source}
          disabled={busy}
          onChange={changeSource}
        />
        {source === 'device' ? (
          <div className="grid gap-1">
            <FilePicker
              ref={picker}
              label={file ? t.changePicture : t.picture}
              {...(fieldError === 'file' ? {} : { hint: t.pictureHint })}
              accept="image/jpeg,image/png,image/webp,image/gif"
              disabled={busy}
              aria-invalid={fieldError === 'file' || undefined}
              {...(fieldError === 'file' ? { 'aria-describedby': fileErrorId } : {})}
              onChange={(event) => {
                setFile(event.currentTarget.files?.[0] ?? null);
                retry.current = null;
                setFieldError(null);
                setError(null);
              }}
            />
            {file ? <p className="text-sm font-medium wrap-anywhere">{file.name}</p> : null}
            {fieldError === 'file' ? (
              <p id={fileErrorId} role="alert" className="text-sm font-medium text-danger">
                {t.pictureRequired}
              </p>
            ) : null}
          </div>
        ) : proofs.kind === 'loading' ? (
          <div role="status" className="grid gap-1">
            <span className="text-sm text-ink-soft">{t.loadingProofPhotos}</span>
            <Skeleton className="h-12 w-full rounded-md" />
          </div>
        ) : proofs.kind === 'failed' ? (
          <div className="grid justify-items-start gap-2">
            <Notice tone="warning">{proofs.message}</Notice>
            <Button
              onClick={() => {
                setProofs({ kind: 'loading' });
                setProofAttempt((value) => value + 1);
              }}
            >
              {ru.ui.retry}
            </Button>
          </div>
        ) : proofs.items.length ? (
          <div className="grid gap-2">
            <Select
              ref={photoPicker}
              label={t.approvedPhoto}
              value={selectedPhoto}
              disabled={busy}
              error={fieldError === 'proof' ? t.pictureRequired : undefined}
              onChange={(event) => {
                setSelectedPhoto(event.currentTarget.value);
                retry.current = null;
                setFieldError(null);
                setError(null);
              }}
            >
              <option value="" disabled>
                {t.chooseProofPhoto}
              </option>
              {proofs.items.map((photo) => {
                const { title } = photo;
                const number = (titleNumbers.get(title) ?? 0) + 1;
                titleNumbers.set(title, number);
                return (
                  <option key={photoKey(photo)} value={photoKey(photo)}>
                    {(titleCounts.get(title) ?? 0) > 1 ? t.proofPhotoOption(title, number) : title}
                  </option>
                );
              })}
            </Select>
            {candidate ? (
              <img
                key={photoKey(candidate)}
                src={candidate.thumbnailUrl}
                alt={t.selectedProofPhoto}
                className="aspect-video max-h-40 w-full rounded-md bg-page object-contain"
              />
            ) : null}
          </div>
        ) : (
          <Notice tone="info">{t.noProofPhotos}</Notice>
        )}
        <TextArea
          ref={captionField}
          label={t.caption}
          value={caption}
          disabled={busy}
          maxLength={maxCaptionLength}
          required
          error={
            fieldError === 'caption'
              ? caption.trim()
                ? t.captionTooLong
                : t.captionRequired
              : undefined
          }
          onChange={(event) => {
            const next = event.currentTarget.value;
            if (next.trim() !== caption.trim()) retry.current = null;
            setCaption(next);
            if (fieldError === 'caption') setFieldError(null);
            setError(null);
          }}
        />
      </form>
    </FormDialog>
  );
}
