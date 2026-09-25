import { useState, type SyntheticEvent } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

// Limits.MaxProofLinks and Limits.MaxProofLinkLength of the engine (D-98)
const maxLinks = 5;
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

/**
 * The proof of the last completed run (D-98): up to five links and a note, or another player who saw the run. While the
 * proof waits for the admin, a new one replaces it; an approved or rejected proof only shows its status.
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
  onSubmit: (links: string[], note: string | null, witnessId: string | null) => void;
}) {
  const [links, setLinks] = useState(['']);
  const [note, setNote] = useState('');
  const [witness, setWitness] = useState('');
  const [error, setError] = useState<string | null>(null);

  function submit(event: SyntheticEvent) {
    event.preventDefault();
    const filled = links.map((l) => l.trim()).filter((l) => l !== '');
    if (filled.length === 0 && witness === '') {
      setError(ru.proof.linkRequired);
      return;
    }
    if (!filled.every(isLink)) {
      setError(ru.proof.linkInvalid);
      return;
    }
    setError(null);
    onSubmit(filled, note.trim() === '' ? null : note.trim(), witness === '' ? null : witness);
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
          <button data-testid="proof-submit" type="submit" disabled={pending}>
            {ru.proof.submit}
          </button>
        </form>
      )}
    </section>
  );
}
