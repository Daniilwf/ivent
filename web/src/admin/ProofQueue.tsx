import { ClipboardCheck, ExternalLink, Flag, Lock } from 'lucide-react';
import { useState } from 'react';
import { api, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ConfirmDanger } from '../ui/Dialogs';
import { Select, TextArea } from '../ui/Field';
import { Badge } from '../ui/Marks';
import { EmptyState, Notice } from '../ui/States';
import { commentProblem, newCommandId, refusal } from './actions';
import { Loading } from './common';
import { type Loaded } from './useLoad';

const t = ru.admin.proofs;

type Item = Schemas['ProofQueueItemView'];
type Difficulty = NonNullable<Schemas['Difficulty']>;

const ladder: Difficulty[] = ['easy', 'normal', 'hard', 'extreme'];

/** The runs to check, finishes on top (D-98): approve, approve without a screenshot, or reject */
export function ProofQueue({ seasonId, loaded }: { seasonId: string; loaded: Loaded<Item[]> }) {
  const [done, setDone] = useState<string | null>(null);
  return (
    <Loading loaded={loaded}>
      {(items, reload) => {
        const closed = [...new Set(items.filter((i) => i.rollClosed).map((i) => i.playerName))];
        return (
          <div className="grid gap-4" data-testid="proof-queue">
            {done ? <Notice tone="success">{done}</Notice> : null}
            {items.length === 0 ? (
              <EmptyState
                level={2}
                icon={<ClipboardCheck size={28} aria-hidden />}
                title={t.emptyTitle}
                text={t.emptyText}
              />
            ) : (
              <>
                <p className="font-medium" data-testid="proof-count">
                  {t.count(items.length)}
                </p>
                {closed.length > 0 ? (
                  <Notice tone="warning">{t.rollClosedSummary(closed.join(', '))}</Notice>
                ) : null}
                <ol className="grid gap-4">
                  {items.map((item, index) => (
                    <li key={item.runId}>
                      <ProofCard
                        seasonId={seasonId}
                        item={item}
                        first={index === 0}
                        onDone={(message) => {
                          setDone(message);
                          reload();
                        }}
                      />
                    </li>
                  ))}
                </ol>
              </>
            )}
          </div>
        );
      }}
    </Loading>
  );
}

/** One run to check: what was claimed, the proof, and the three decisions */
export function ProofCard({
  seasonId,
  item,
  first,
  onDone,
}: {
  seasonId: string;
  item: Item;
  first: boolean;
  onDone: (message: string) => void;
}) {
  const sent = item.status === 'pending';
  const [difficulty, setDifficulty] = useState<Difficulty | ''>('');
  const [comment, setComment] = useState('');
  const [commentError, setCommentError] = useState<string>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<'approve' | 'reject' | null>(null);
  const [rejecting, setRejecting] = useState(false);
  const [rejectComment, setRejectComment] = useState('');
  const [rejectError, setRejectError] = useState<string>();
  const [rejectRefusal, setRejectRefusal] = useState<string>();

  const claimed = item.difficulty ?? null;
  const lower = claimed ? ladder.slice(0, ladder.indexOf(claimed)) : [];
  const path = { seasonId, runId: item.runId };

  async function approve() {
    // Without a proof the log needs the reason (D-98: «одобрить без скрина»)
    const text = comment.trim();
    if (!sent) {
      const problem = commentProblem(text);
      setCommentError(problem);
      if (problem) return;
    } else if (text.length > 500) {
      setCommentError(ru.admin.commentTooLong);
      return;
    }
    setBusy('approve');
    setError(undefined);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/runs/{runId}/approve', {
        params: { path },
        body: {
          commandId: newCommandId(),
          difficulty: difficulty === '' ? null : difficulty,
          comment: text === '' ? null : text,
        },
      });
      if (answer.data) onDone(t.approved(item.gameTitle));
      else setError(refusal(answer));
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  async function reject() {
    const problem = commentProblem(rejectComment);
    setRejectError(problem);
    if (problem) return;
    setBusy('reject');
    setRejectRefusal(undefined);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/runs/{runId}/reject', {
        params: { path },
        body: { commandId: newCommandId(), comment: rejectComment.trim() },
      });
      // A refusal stays in the dialog, like the undo's
      if (answer.data) {
        setRejecting(false);
        onDone(t.rejected(item.gameTitle));
      } else setRejectRefusal(refusal(answer));
    } catch {
      setRejectRefusal(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  return (
    <article
      className="grid gap-4 rounded-lg bg-card p-4 wrap-anywhere"
      data-testid={`proof-${item.runId}`}
      aria-labelledby={`proof-title-${item.runId}`}
    >
      <header className="grid gap-2">
        <div className="flex flex-wrap items-center gap-2">
          {item.decidesFinish ? (
            <Badge>
              <Flag size={12} aria-hidden className="mr-1" />
              {t.decidesFinish}
            </Badge>
          ) : null}
          {item.rollClosed ? (
            <span
              data-testid="roll-closed"
              className="inline-flex items-center gap-1 rounded-full bg-warning-soft px-2 text-xs font-bold text-ink"
            >
              <Lock size={12} aria-hidden className="text-warning" />
              {t.rollClosed}
            </span>
          ) : null}
        </div>
        <h2 id={`proof-title-${item.runId}`} className="font-display text-lg font-heavy">
          {item.gameTitle}
        </h2>
        <p className="font-bold">{item.playerName}</p>
        {item.completedAt ? (
          <p className="text-sm text-ink-soft">{t.completed(moscowTime(item.completedAt))}</p>
        ) : null}
        <p className="text-sm text-ink-soft">
          {[
            claimed ? t.claimed(ru.difficulty[claimed]) : null,
            t.hours(item.hours),
            t.dice(item.diceTotal),
          ]
            .filter((part) => part !== null)
            .join(', ')}
        </p>
      </header>

      <section className="grid gap-2" aria-label={ru.proof.title}>
        <p className="font-medium">{sent ? t.proofSent : t.noProof}</p>
        {item.links.length > 0 ? (
          <ul className="grid gap-1">
            {item.links.map((link, i) => (
              <li key={link}>
                <a
                  href={link}
                  target="_blank"
                  rel="noreferrer noopener"
                  className="inline-flex min-h-11 items-center gap-2 rounded-sm font-medium break-all underline underline-offset-4 is-focus:focus-ring"
                >
                  <ExternalLink size={16} aria-hidden className="shrink-0" />
                  {t.link(i + 1)}: {link}
                </a>
              </li>
            ))}
          </ul>
        ) : null}
        {item.files.length > 0 ? (
          <ul className="flex flex-wrap gap-2">
            {item.files.map((file, i) => (
              <li key={file.id}>
                <a
                  href={file.url}
                  target="_blank"
                  rel="noreferrer noopener"
                  className="block rounded-md is-focus:focus-ring"
                >
                  <img
                    src={file.thumbnailUrl}
                    alt={t.shot(i + 1)}
                    width={96}
                    height={96}
                    className="size-24 rounded-md border-2 border-ink object-cover"
                  />
                </a>
              </li>
            ))}
          </ul>
        ) : null}
        {item.note ? <p>{t.note(item.note)}</p> : null}
        {item.witnessName ? <p>{t.witness(item.witnessName)}</p> : null}
      </section>

      <div className="grid gap-3 desk:grid-cols-2">
        {lower.length > 0 ? (
          <Select
            label={t.difficulty}
            hint={t.difficultyHint}
            value={difficulty}
            data-testid="approve-difficulty"
            onChange={(e) => {
              setDifficulty(e.target.value as Difficulty | '');
            }}
          >
            <option value="">{claimed ? ru.difficulty[claimed] : ''}</option>
            {lower.map((d) => (
              <option key={d} value={d}>
                {ru.difficulty[d]}
              </option>
            ))}
          </Select>
        ) : null}
        <TextArea
          label={ru.admin.comment}
          hint={sent ? ru.admin.commentHint : t.withoutShotHint}
          value={comment}
          rows={2}
          maxLength={500}
          error={commentError}
          data-testid="approve-comment"
          onChange={(e) => {
            setComment(e.target.value);
          }}
          className="desk:col-span-2"
        />
      </div>

      {error ? <Notice tone="danger">{error}</Notice> : null}

      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant={first ? 'main' : 'quiet'}
          loading={busy === 'approve'}
          disabled={busy === 'reject'}
          data-testid="approve"
          onClick={() => void approve()}
        >
          {sent ? t.approve : t.approveWithoutShot}
        </Button>
        <ConfirmDanger
          open={rejecting}
          onOpenChange={(open) => {
            setRejecting(open);
            if (!open) {
              setRejectError(undefined);
              setRejectRefusal(undefined);
            }
          }}
          trigger={
            <Button variant="danger" data-testid="reject" disabled={busy === 'approve'}>
              {t.reject}
            </Button>
          }
          title={t.rejectTitle(item.gameTitle, item.playerName)}
          consequences={t.rejectConsequences(item.diceTotal, item.decidesFinish)}
          confirm={t.rejectConfirm}
          busy={busy === 'reject'}
          onConfirm={() => void reject()}
        >
          <TextArea
            label={ru.admin.comment}
            hint={t.rejectCommentHint}
            value={rejectComment}
            rows={2}
            maxLength={500}
            error={rejectError}
            data-testid="reject-comment"
            onChange={(e) => {
              setRejectComment(e.target.value);
            }}
          />
          {rejectRefusal ? <Notice tone="danger">{rejectRefusal}</Notice> : null}
        </ConfirmDanger>
      </div>
    </article>
  );
}
