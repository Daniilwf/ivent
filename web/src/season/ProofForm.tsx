import { useRef, useState, type ChangeEvent, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { uploadFile, UploadError } from '../api/files';
import { ru } from '../i18n/ru';

// Limits.MaxProofLinks, Limits.MaxProofFiles and Limits.MaxProofLinkLength of the engine (D-98, D-116)
const maxLinks = 5;
const maxFiles = 5;
const pictureTypes = 'image/png,image/jpeg,image/webp,image/gif';
const maxLinkLength = 500;
const maxNoteLength = 500;

const isLink = (value: string) => {
  try {
    const url = new URL(value);
    return (url.protocol === 'http:' || url.protocol === 'https:') && url.hostname !== '';
  } catch {
    return false;
  }
};

type Shot = { id: string; thumbnailUrl: string };

/**
 * The proof of the last completed run (D-98, D-116): up to five links, up to five uploaded screenshots and a note, or
 * another player who saw the run. While the proof waits for the admin, a new one replaces it; an approved or rejected
 * proof only shows its status.
 */
export function ProofSection({
  proof,
  witnesses,
  pending,
  onSubmit,
}: {
  proof: Schemas['ProofView'] | null;
  witnesses: readonly Schemas['PlayerView'][];
  pending: boolean;
  onSubmit: (
    links: string[],
    note: string | null,
    witnessId: string | null,
    files: string[],
  ) => void;
}) {
  const [links, setLinks] = useState(['']);
  const [shots, setShots] = useState<Shot[]>([]);
  const [uploading, setUploading] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const [note, setNote] = useState('');
  const [witness, setWitness] = useState('');
  const [error, setError] = useState<string | null>(null);

  function submit(event: SyntheticEvent) {
    event.preventDefault();
    const filled = links.map((l) => l.trim()).filter((l) => l !== '');
    if (filled.length === 0 && shots.length === 0 && witness === '') {
      setError(ru.proof.linkRequired);
      return;
    }
    if (!filled.every(isLink)) {
      setError(ru.proof.linkInvalid);
      return;
    }
    setError(null);
    onSubmit(
      filled,
      note.trim() === '' ? null : note.trim(),
      witness === '' ? null : witness,
      shots.map((s) => s.id),
    );
  }

  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const input = event.currentTarget;
    const picked = input.files?.[0];
    input.value = '';
    if (!picked) {
      return;
    }
    setUploading(true);
    setError(null);
    try {
      const stored = await uploadFile(picked);
      setShots((current) =>
        current.some((s) => s.id === stored.id)
          ? current
          : [...current, { id: stored.id, thumbnailUrl: stored.thumbnailUrl }],
      );
    } catch (e) {
      const code = e instanceof UploadError ? e.code : null;
      setError(
        (code && ru.upload.errors[code as keyof typeof ru.upload.errors]) ?? ru.upload.failed,
      );
    } finally {
      setUploading(false);
    }
  }

  const open = proof === null || proof.status === 'pending';
  return (
    <section aria-labelledby="proof-title">
      <h3 id="proof-title">{ru.proof.title}</h3>
      {proof && (
        <p data-testid="proof-status">
          {ru.proof.status[proof.status]}
          {proof.comment ? ` ${ru.proof.reviewComment(proof.comment)}` : ''}
        </p>
      )}
      {proof && proof.files.length > 0 && (
        <ul data-testid="proof-files">
          {proof.files.map((f, i) => (
            <li key={f.id}>
              <a href={f.url} target="_blank" rel="noreferrer">
                <img src={f.thumbnailUrl} alt={ru.proof.shotAlt(i + 1)} />
              </a>
            </li>
          ))}
        </ul>
      )}
      {open && (
        <form onSubmit={submit} data-testid="proof-form">
          {links.map((value, i) => (
            <label key={i}>
              {ru.proof.link}
              <input
                data-testid="proof-link"
                type="url"
                maxLength={maxLinkLength}
                value={value}
                onChange={(e) => {
                  setLinks(links.map((l, j) => (j === i ? e.target.value : l)));
                }}
              />
            </label>
          ))}
          {links.length < maxLinks && (
            <button
              type="button"
              onClick={() => {
                setLinks([...links, '']);
              }}
            >
              {ru.proof.addLink}
            </button>
          )}
          {shots.length > 0 && (
            <ul data-testid="proof-shots">
              {shots.map((s, i) => (
                <li key={s.id}>
                  <img src={s.thumbnailUrl} alt={ru.proof.shotAlt(i + 1)} />
                  <button
                    type="button"
                    aria-label={ru.proof.removeShot(i + 1)}
                    onClick={() => {
                      setShots((current) => current.filter((x) => x.id !== s.id));
                      // The button is gone: the keyboard goes on from the file field
                      requestAnimationFrame(() => fileInput.current?.focus());
                    }}
                  >
                    {ru.proof.remove}
                  </button>
                </li>
              ))}
            </ul>
          )}
          {shots.length < maxFiles && (
            <label>
              {ru.proof.shot}
              <input
                ref={fileInput}
                data-testid="proof-file"
                type="file"
                accept={pictureTypes}
                disabled={uploading}
                onChange={(e) => {
                  void upload(e);
                }}
              />
            </label>
          )}
          <p role="status">{uploading ? ru.upload.uploading : ''}</p>
          <label>
            {ru.proof.note}
            <textarea
              data-testid="proof-note"
              maxLength={maxNoteLength}
              value={note}
              onChange={(e) => {
                setNote(e.target.value);
              }}
            />
          </label>
          {witnesses.length > 0 && (
            <label>
              {ru.proof.witness}
              <select
                data-testid="proof-witness"
                value={witness}
                onChange={(e) => {
                  setWitness(e.target.value);
                }}
              >
                <option value="">{ru.proof.noWitness}</option>
                {witnesses.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          {error && <p role="alert">{error}</p>}
          <button data-testid="proof-submit" type="submit" disabled={pending || uploading}>
            {ru.proof.submit}
          </button>
        </form>
      )}
    </section>
  );
}
