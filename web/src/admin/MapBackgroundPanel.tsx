import { ImageOff } from 'lucide-react';
import { useState, type ChangeEvent } from 'react';
import type { Schemas } from '../api/client';
import { uploadFile, UploadError } from '../api/files';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { FilePicker } from '../ui/Field';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';

const t = ru.admin.map.background;

export type MapBackground = Schemas['MapBackgroundView'];

/** Still pictures only: a map background is never an animation (D-278) */
const pictureTypes = 'image/png,image/jpeg,image/webp';

/** What the upload is doing; `state` of the styleguide shows one without a server */
export type BackgroundUpload =
  { kind: 'idle' } | { kind: 'uploading' } | { kind: 'failed'; reason: string };

/**
 * The picture under the map in the editor (SPEC «Редактор»: загрузка фона; D-277, D-278): none, uploading, failed,
 * there. A new picture goes into the draft — players see it when the map is published; «Убрать фон» takes it out of
 * the draft the same way. Everything is a button or a file field: the keyboard reaches it all.
 */
export function MapBackgroundPanel({
  seasonId,
  background,
  onChange,
  pending = false,
  state,
  preview,
}: {
  seasonId: string;
  background: MapBackground | null;
  onChange: (next: MapBackground | null) => void;
  /** The draft's picture differs from the published one: players see it after the publication */
  pending?: boolean;
  /** A fixed state for the styleguide */
  state?: BackgroundUpload;
  /** The styleguide's picture instead of the server's thumbnail */
  preview?: string;
}) {
  const [upload, setUpload] = useState<BackgroundUpload>({ kind: 'idle' });
  const shown = state ?? upload;
  const busy = shown.kind === 'uploading';

  function pick(event: ChangeEvent<HTMLInputElement>) {
    const input = event.currentTarget;
    const picked = input.files?.[0];
    input.value = '';
    if (!picked) return;
    setUpload({ kind: 'uploading' });
    uploadFile(picked, picked.name, `/api/admin/seasons/${seasonId}/map/background`)
      .then((stored) => {
        setUpload({ kind: 'idle' });
        onChange({
          fileId: stored.id,
          width: stored.width,
          height: stored.height,
          url: stored.url,
        });
      })
      .catch((e: unknown) => {
        const code = e instanceof UploadError ? e.code : null;
        setUpload({
          kind: 'failed',
          reason:
            (code && ru.upload.errors[code as keyof typeof ru.upload.errors]) ?? ru.upload.failed,
        });
      });
  }

  return (
    <Panel title={t.title} data-testid="map-background" aria-busy={busy || undefined}>
      {background ? (
        <figure className="grid justify-items-start gap-2">
          <img
            src={preview ?? `/api/files/${background.fileId}/thumbnail`}
            alt={t.preview}
            className="max-h-40 w-auto rounded-md border-2 border-ink bg-page"
            data-testid="map-background-preview"
          />
          <figcaption className="text-sm text-ink-soft" data-testid="map-background-size">
            {t.size(background.width, background.height)}
          </figcaption>
        </figure>
      ) : (
        <p
          className="flex items-start gap-2 text-sm text-ink-soft"
          data-testid="map-background-none"
        >
          <ImageOff size={20} aria-hidden className="shrink-0" />
          {t.none}
        </p>
      )}
      <p className="text-sm text-ink-soft">{background ? t.placement : t.formats}</p>
      <div className="flex flex-wrap items-center gap-3">
        <FilePicker
          label={background ? t.replace : t.upload}
          accept={pictureTypes}
          busy={busy}
          data-testid="map-background-file"
          onChange={pick}
        />
        {background ? (
          <Button
            variant="link"
            disabled={busy}
            data-testid="map-background-remove"
            onClick={() => {
              setUpload({ kind: 'idle' });
              onChange(null);
            }}
          >
            {t.remove}
          </Button>
        ) : null}
      </div>
      {busy ? (
        <p role="status" className="text-sm text-ink-soft">
          {t.uploading}
        </p>
      ) : null}
      {shown.kind === 'failed' ? <Notice tone="danger">{shown.reason}</Notice> : null}
      {pending && shown.kind !== 'failed' ? <Notice tone="info">{t.draftOnly}</Notice> : null}
    </Panel>
  );
}
