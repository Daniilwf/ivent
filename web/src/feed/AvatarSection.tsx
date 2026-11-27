import { useState, type ChangeEvent, type SyntheticEvent } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { uploadFile, UploadError } from '../api/files';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Field, FilePicker } from '../ui/Field';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';

const pictureTypes = 'image/png,image/jpeg,image/webp,image/gif';

// The pages of the GIF sites, not their media hosts: the server takes only the GIF itself (SafeDownloader, K1A)
const gifPages = ['tenor.com', 'giphy.com', 'klipy.com'];

function isGifPage(url: string): boolean {
  try {
    const host = new URL(url).hostname.toLowerCase().replace(/^www\./, '');
    return gifPages.includes(host);
  } catch {
    return false;
  }
}

/**
 * The signed-in user's own avatar (D-117) — a player's, a spectator's or the admin's: a picture from the device or a GIF
 * by link from Tenor, Giphy or Klipy (the server downloads it), or none. The avatar is on the account, so it stays across
 * seasons; it is read from the account itself, not from a season.
 */
export function AvatarSection({
  avatar,
  onChanged,
}: {
  avatar: Schemas['FileLinkView'] | null;
  onChanged: (avatar: Schemas['FileLinkView'] | null) => void;
}) {
  const [link, setLink] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function use(fileId: string | null) {
    const { data, error: refused } = await api.PUT('/api/auth/me/avatar', {
      body: { commandId: newCommandId(), fileId },
    });
    if (!data) {
      const code = rejectionCode(refused);
      setError((code && ru.rejection[code]) ?? ru.rejection.unknown);
      return;
    }
    onChanged(data.avatar);
  }

  async function run(step: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await step();
    } catch (e) {
      const code = e instanceof UploadError ? e.code : null;
      setError(
        (code && ru.upload.errors[code as keyof typeof ru.upload.errors]) ?? ru.upload.failed,
      );
    } finally {
      setBusy(false);
    }
  }

  function pick(event: ChangeEvent<HTMLInputElement>) {
    const input = event.currentTarget;
    const picked = input.files?.[0];
    input.value = '';
    if (picked) {
      void run(async () => {
        const stored = await uploadFile(picked);
        await use(stored.id);
      });
    }
  }

  function byLink(event: SyntheticEvent) {
    event.preventDefault();
    const url = link.trim();
    if (url === '') {
      setError(ru.avatar.linkRequired);
      return;
    }
    if (isGifPage(url)) {
      setError(ru.avatar.pageLink);
      return;
    }
    void run(async () => {
      const {
        data,
        error: refused,
        response,
      } = await api.POST('/api/files/from-url', {
        body: { commandId: newCommandId(), url },
      });
      if (!data) {
        // The hourly limit of downloads answers without a code (D-117)
        throw new UploadError(
          rejectionCode(refused) ?? (response.status === 429 ? 'file.downloadsPerHour' : null),
        );
      }
      setLink('');
      await use(data.id);
    });
  }

  return (
    <Panel title={ru.avatar.title} aria-label={ru.avatar.title} data-testid="avatar">
      {avatar ? (
        <div className="flex flex-wrap items-center gap-3">
          <img
            src={avatar.thumbnailUrl}
            alt={ru.avatar.current}
            width={64}
            height={64}
            className="size-16 rounded-full object-cover"
          />
          <Button
            disabled={busy}
            onClick={() => {
              void run(() => use(null));
            }}
          >
            {ru.avatar.remove}
          </Button>
        </div>
      ) : (
        <p className="text-sm text-ink-soft">{ru.avatar.none}</p>
      )}
      {/* A Russian button over the browser's own field (H4, the H2 design review) */}
      <FilePicker
        data-testid="avatar-file"
        label={ru.avatar.file}
        accept={pictureTypes}
        busy={busy}
        onChange={pick}
      />
      <form onSubmit={byLink} noValidate className="grid min-w-0 gap-2">
        <Field
          data-testid="avatar-link"
          label={ru.avatar.link}
          type="url"
          maxLength={2000}
          value={link}
          onChange={(e) => {
            setLink(e.target.value);
          }}
        />
        <Button type="submit" disabled={busy} className="justify-self-start">
          {ru.avatar.useLink}
        </Button>
      </form>
      <p role="status" className={busy ? undefined : 'sr-only'}>
        {busy ? ru.upload.uploading : ''}
      </p>
      {error && <Notice tone="danger">{error}</Notice>}
    </Panel>
  );
}
