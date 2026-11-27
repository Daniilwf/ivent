import { useState } from 'react';
import type { Schemas } from '../../api/client';
import { newCommandId } from '../../api/commands';
import { ru } from '../../i18n/ru';
import { Button } from '../../ui/Button';
import { ConfirmDanger } from '../../ui/Dialogs';
import { TextArea } from '../../ui/Field';
import { Notice } from '../../ui/States';
import { commentProblem, refusal } from '../actions';

type ReviewAnswer = { data?: Schemas['CommandResponse']; error?: unknown; response: Response };

export function ReviewActions({
  first,
  rejectTitle,
  rejectConsequence,
  send,
  onDone,
}: {
  first: boolean;
  rejectTitle: string;
  rejectConsequence: string;
  send: (commandId: string, approved: boolean, comment: string | null) => Promise<ReviewAnswer>;
  onDone: (approved: boolean, response: Schemas['CommandResponse']) => void;
}) {
  const [rejecting, setRejecting] = useState(false);
  const [busy, setBusy] = useState<'approve' | 'reject' | null>(null);
  const [comment, setComment] = useState('');
  const [commentError, setCommentError] = useState<string>();
  const [approveError, setApproveError] = useState<string>();
  const [rejectError, setRejectError] = useState<string>();

  async function review(approved: boolean) {
    if (busy !== null) return;
    const note = approved ? null : comment.trim();
    if (note !== null) {
      const problem = commentProblem(note);
      setCommentError(problem);
      if (problem) return;
    }
    const setError = approved ? setApproveError : setRejectError;
    setBusy(approved ? 'approve' : 'reject');
    setError(undefined);
    try {
      const answer = await send(newCommandId(), approved, note);
      if (!answer.data) {
        setError(refusal(answer));
        return;
      }
      setRejecting(false);
      setComment('');
      onDone(approved, answer.data);
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  return (
    <>
      {approveError ? <Notice tone="danger">{approveError}</Notice> : null}
      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant={first ? 'main' : 'quiet'}
          loading={busy === 'approve'}
          disabled={busy === 'reject'}
          onClick={() => void review(true)}
        >
          {ru.admin.proofs.approve}
        </Button>
        <ConfirmDanger
          open={rejecting}
          onOpenChange={(open) => {
            setRejecting(open);
            if (!open) {
              setCommentError(undefined);
              setRejectError(undefined);
            }
          }}
          trigger={
            <Button variant="danger" disabled={busy !== null}>
              {ru.admin.proofs.reject}
            </Button>
          }
          title={rejectTitle}
          consequences={[rejectConsequence]}
          confirm={ru.admin.proofs.reject}
          busy={busy === 'reject'}
          onConfirm={() => void review(false)}
        >
          <TextArea
            label={ru.admin.comment}
            value={comment}
            disabled={busy === 'reject'}
            rows={2}
            maxLength={500}
            error={commentError}
            onChange={(event) => {
              setComment(event.target.value);
            }}
          />
          {rejectError ? <Notice tone="danger">{rejectError}</Notice> : null}
        </ConfirmDanger>
      </div>
    </>
  );
}
