import { CircleAlert, CircleCheck, Clock, Plus, X } from 'lucide-react';
import {
  useId,
  useRef,
  useState,
  type ChangeEvent,
  type ReactNode,
  type SyntheticEvent,
} from 'react';
import type { Schemas } from '../api/client';
import { uploadFile, UploadError } from '../api/files';
import { ru } from '../i18n/ru';
import { CompletionRulesLink } from '../rules/CompletionRulesLink';
import { Button, IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import { Field, FilePicker, Select, TextArea } from '../ui/Field';

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
type Problem = { at: 'form' | 'file' | number; text: string };

const statusLook: Record<Schemas['ProofStatus'], { box: string; icon: ReactNode }> = {
  pending: {
    box: 'border-info bg-info-soft',
    icon: <Clock size={20} aria-hidden className="text-info" />,
  },
  approved: {
    box: 'border-success bg-success-soft',
    icon: <CircleCheck size={20} aria-hidden className="text-success" />,
  },
  rejected: {
    box: 'border-danger bg-danger-soft',
    icon: <CircleAlert size={20} aria-hidden className="text-danger" />,
  },
};

/**
 * The proof of a completed run (D-98, D-116): up to five links, up to five uploaded screenshots and a note, or
 * another player who saw the run. While the proof waits for the admin, a new one replaces it: the form starts with what
 * was sent — its links, note, witness and screenshots (each can be taken off) — so adding a link keeps the rest (K1S-2).
 * An approved or rejected proof only shows its status. A mistake is told next to its field. The parent gives the form a
 * new key when the sent proof changes.
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
  const sent = proof?.status === 'pending' ? proof : null;
  const [links, setLinks] = useState(sent && sent.links.length > 0 ? [...sent.links] : ['']);
  // The screenshots of the sent proof that stay in the next one
  const [kept, setKept] = useState<readonly Schemas['FileLinkView'][]>(sent?.files ?? []);
  const [shots, setShots] = useState<Shot[]>([]);
  const [uploading, setUploading] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const [note, setNote] = useState(sent?.note ?? '');
  const [witness, setWitness] = useState(
    sent?.witnessId && witnesses.some((p) => p.id === sent.witnessId) ? sent.witnessId : '',
  );
  const [problem, setProblem] = useState<Problem | null>(null);
  const titleId = useId();

  function submit(event: SyntheticEvent) {
    event.preventDefault();
    const trimmed = links.map((l) => l.trim());
    const filled = trimmed.filter((l) => l !== '');
    if (filled.length === 0 && shots.length === 0 && kept.length === 0 && witness === '') {
      setProblem({ at: 'form', text: ru.proof.linkRequired });
      return;
    }
    const wrong = trimmed.findIndex((l) => l !== '' && !isLink(l));
    if (wrong >= 0) {
      setProblem({ at: wrong, text: ru.proof.linkInvalid });
      return;
    }
    setProblem(null);
    onSubmit(filled, note.trim() === '' ? null : note.trim(), witness === '' ? null : witness, [
      ...kept.map((f) => f.id),
      ...shots.map((s) => s.id),
    ]);
  }

  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const input = event.currentTarget;
    const picked = input.files?.[0];
    input.value = '';
    if (!picked) {
      return;
    }
    setUploading(true);
    setProblem(null);
    try {
      const stored = await uploadFile(picked);
      setShots((current) =>
        current.some((s) => s.id === stored.id)
          ? current
          : [...current, { id: stored.id, thumbnailUrl: stored.thumbnailUrl }],
      );
    } catch (e) {
      const code = e instanceof UploadError ? e.code : null;
      setProblem({
        at: 'file',
        text: (code && ru.upload.errors[code as keyof typeof ru.upload.errors]) ?? ru.upload.failed,
      });
    } finally {
      setUploading(false);
    }
  }

  const open = proof === null || proof.status === 'pending';
  return (
    <section aria-labelledby={titleId} className="grid min-w-0 gap-3">
      <h4 id={titleId} className="sr-only">
        {ru.proof.title}
      </h4>
      {proof && (
        <div
          data-testid="proof-status"
          className={cx(
            'flex items-start gap-2 rounded-md border-2 px-3 py-2 text-sm font-medium',
            statusLook[proof.status].box,
          )}
        >
          {statusLook[proof.status].icon}
          <p className="grid gap-1">
            <span>{ru.proof.status[proof.status]}</span>
            {proof.comment ? <span>{ru.proof.reviewComment(proof.comment)}</span> : null}
          </p>
        </div>
      )}
      {proof && (open ? kept : proof.files).length > 0 && (
        <ul data-testid="proof-files" className="flex flex-wrap gap-2">
          {(open ? kept : proof.files).map((f, i) => (
            <li key={f.id} className="grid justify-items-center gap-1">
              <a
                href={f.url}
                target="_blank"
                rel="noreferrer"
                className="block rounded-sm is-focus:focus-ring"
              >
                <img
                  src={f.thumbnailUrl}
                  alt={ru.proof.shotAlt(i + 1)}
                  width={72}
                  height={72}
                  className="size-18 rounded-sm border-2 border-ink object-cover"
                />
              </a>
              {/* A screenshot of the waiting proof stays in the next send unless taken off */}
              {open ? (
                <IconButton
                  label={ru.proof.removeShot(i + 1)}
                  onClick={() => {
                    setKept((current) => current.filter((x) => x.id !== f.id));
                    requestAnimationFrame(() => fileInput.current?.focus());
                  }}
                >
                  <X size={20} aria-hidden />
                </IconButton>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      {open && (
        <form onSubmit={submit} noValidate data-testid="proof-form" className="grid min-w-0 gap-4">
          <p className="text-sm text-ink-soft">{sent ? ru.proof.replaces : ru.proof.lead}</p>
          <CompletionRulesLink className="justify-self-start" />
          {links.map((value, i) => (
            <Field
              key={i}
              data-testid="proof-link"
              label={links.length > 1 ? ru.proof.linkNumber(i + 1) : ru.proof.link}
              type="url"
              inputMode="url"
              maxLength={maxLinkLength}
              value={value}
              error={problem?.at === i ? problem.text : undefined}
              onChange={(e) => {
                setLinks(links.map((l, j) => (j === i ? e.target.value : l)));
              }}
            />
          ))}
          {links.length < maxLinks && (
            <div>
              <Button
                variant="link"
                icon={<Plus size={20} aria-hidden />}
                onClick={() => {
                  setLinks([...links, '']);
                }}
              >
                {ru.proof.addLink}
              </Button>
            </div>
          )}
          {shots.length > 0 && (
            <ul data-testid="proof-shots" className="flex flex-wrap gap-3">
              {shots.map((s, i) => (
                <li key={s.id} className="grid justify-items-center gap-1">
                  <img
                    src={s.thumbnailUrl}
                    alt={ru.proof.shotAlt(kept.length + i + 1)}
                    width={72}
                    height={72}
                    className="size-18 rounded-sm border-2 border-ink object-cover"
                  />
                  <IconButton
                    label={ru.proof.removeShot(kept.length + i + 1)}
                    onClick={() => {
                      setShots((current) => current.filter((x) => x.id !== s.id));
                      // The button is gone: the keyboard goes on from the file picker
                      requestAnimationFrame(() => fileInput.current?.focus());
                    }}
                  >
                    <X size={20} aria-hidden />
                  </IconButton>
                </li>
              ))}
            </ul>
          )}
          {kept.length + shots.length < maxFiles && (
            <FilePicker
              ref={fileInput}
              data-testid="proof-file"
              label={ru.proof.shot}
              hint={ru.proof.shotHint(maxFiles - kept.length - shots.length)}
              accept={pictureTypes}
              busy={uploading}
              onChange={(e) => {
                void upload(e);
              }}
            />
          )}
          <p role="status" className="text-sm text-ink-soft empty:hidden">
            {uploading ? ru.upload.uploading : ''}
          </p>
          {problem?.at === 'file' ? (
            <p role="alert" className="text-sm font-medium text-danger">
              {problem.text}
            </p>
          ) : null}
          {witnesses.length > 0 && (
            <Select
              data-testid="proof-witness"
              label={ru.proof.witness}
              hint={ru.proof.witnessHint}
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
            </Select>
          )}
          <TextArea
            data-testid="proof-note"
            label={ru.proof.note}
            maxLength={maxNoteLength}
            rows={3}
            value={note}
            onChange={(e) => {
              setNote(e.target.value);
            }}
          />
          {problem?.at === 'form' ? (
            <p role="alert" className="text-sm font-medium text-danger">
              {problem.text}
            </p>
          ) : null}
          <Button
            data-testid="proof-submit"
            type="submit"
            loading={pending}
            disabled={pending || uploading}
          >
            {ru.proof.submit}
          </Button>
        </form>
      )}
    </section>
  );
}
