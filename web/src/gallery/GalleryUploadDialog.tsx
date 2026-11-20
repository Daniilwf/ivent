import { useEffect, useId, useRef, useState, type SubmitEvent, type RefObject } from 'react';
import { newCommandId } from '../api/commands';
import { UploadError } from '../api/files';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { FormDialog } from '../ui/Dialogs';
import { FilePicker, TextArea } from '../ui/Field';
import { Notice } from '../ui/States';
import { publishMoment, type GalleryMoment } from './galleryApi';

const t = ru.gallery;
const maxCaptionLength = 500;

function uploadIssue(code: string | null) {
  if (code === 'gallery.dailyLimit') return t.dailyLimit;
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
  const captionField = useRef<HTMLTextAreaElement>(null);
  const [caption, setCaption] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [fieldError, setFieldError] = useState<'file' | 'caption' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const inFlight = useRef<AbortController | null>(null);
  const retry = useRef<{ commandId: string; itemId: string } | null>(null);
  useEffect(() => () => inFlight.current?.abort(), []);

  async function submit(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    if (inFlight.current) return;
    if (!file) {
      setFieldError('file');
      picker.current?.focus();
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
      const moment = await publishMoment(seasonId, ids, file, trimmed, controller.signal);
      if (!controller.signal.aborted) onPublished(moment);
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
          <Button type="submit" form={formId} variant="main" loading={busy}>
            {t.publish}
          </Button>
        </>
      }
    >
      <form id={formId} className="grid gap-4" noValidate onSubmit={(event) => void submit(event)}>
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
