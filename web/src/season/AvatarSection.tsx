import { useState, type ChangeEvent, type SyntheticEvent } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { uploadFile, UploadError } from '../api/files';
import { ru } from '../i18n/ru';

const pictureTypes = 'image/png,image/jpeg,image/webp,image/gif';

/**
 * The player's own avatar (D-117): a picture from the device or a GIF by link from Tenor, Giphy or Klipy (the server
 * downloads it), or none. The avatar is on the account, so it stays across seasons.
 */
export function AvatarSection({
  avatar,
  onChanged,
}: {
  avatar: Schemas['FileLinkView'] | null;
  onChanged: () => void;
}) {
  const [link, setLink] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function use(fileId: string | null) {
    const { error: refused } = await api.PUT('/api/auth/me/avatar', {
      body: { commandId: crypto.randomUUID(), fileId },
    });
    if (refused) {
      const code = rejectionCode(refused);
      setError((code && ru.rejection[code]) ?? ru.rejection.unknown);
      return;
    }
    onChanged();
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
    void run(async () => {
      const { data, error: refused } = await api.POST('/api/files/from-url', {
        body: { commandId: crypto.randomUUID(), url },
      });
      if (!data) {
        throw new UploadError(rejectionCode(refused));
      }
      setLink('');
      await use(data.id);
    });
  }

  return (
    <section aria-labelledby="avatar-title" data-testid="avatar">
      <h2 id="avatar-title">{ru.avatar.title}</h2>
      {avatar ? (
        <p>
          <img src={avatar.thumbnailUrl} alt={ru.avatar.current} width={64} height={64} />
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              void run(() => use(null));
            }}
          >
            {ru.avatar.remove}
          </button>
        </p>
      ) : (
        <p>{ru.avatar.none}</p>
      )}
      <label>
        {ru.avatar.file}
        <input
          data-testid="avatar-file"
          type="file"
          accept={pictureTypes}
          disabled={busy}
          onChange={pick}
        />
      </label>
      <form onSubmit={byLink}>
        <label>
          {ru.avatar.link}
          <input
            data-testid="avatar-link"
            type="url"
            maxLength={2000}
            value={link}
            onChange={(e) => {
              setLink(e.target.value);
            }}
          />
        </label>
        <button type="submit" disabled={busy}>
          {ru.avatar.useLink}
        </button>
      </form>
      <p role="status">{busy ? ru.upload.uploading : ''}</p>
      {error && <p role="alert">{error}</p>}
    </section>
  );
}
