import { History, Undo2 } from 'lucide-react';
import { useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ConfirmDanger } from '../ui/Dialogs';
import { TextArea } from '../ui/Field';
import { EmptyState, Notice } from '../ui/States';
import { commentProblem, newCommandId, refusal, related, commandLabel } from './actions';
import { Loading } from './common';
import { useLoad } from './useLoad';

const t = ru.admin.log;

type Command = Schemas['AdminCommandView'];

const step = 50;
const most = 500;

// Neither the season's creation nor an undo can be undone (undo.notUndoable)
const final = new Set(['CreateSeason', 'UndoCommand']);

/** The season log, newest first, with undo of a whole command (D-104) */
export function LogSection({ seasonId, version }: { seasonId: string; version: number }) {
  const [limit, setLimit] = useState(step);
  const loaded = useLoad(
    async () =>
      (
        await api.GET('/api/admin/seasons/{seasonId}/commands', {
          params: { path: { seasonId }, query: { limit } },
        })
      ).data,
    [seasonId, limit],
    version,
  );
  const [done, setDone] = useState<string | null>(null);

  return (
    <Loading loaded={loaded}>
      {(commands, reload) => (
        <div className="grid gap-4" data-testid="admin-log">
          {done ? <Notice tone="success">{done}</Notice> : null}
          {commands.length === 0 ? (
            <EmptyState
              level={2}
              icon={<History size={28} aria-hidden />}
              title={t.emptyTitle}
              text={t.emptyText}
            />
          ) : (
            <ol className="grid gap-2">
              {commands.map((c) => (
                <li key={c.commandId}>
                  <LogRow
                    seasonId={seasonId}
                    command={c}
                    all={commands}
                    onUndone={() => {
                      setDone(t.undoneOk(commandLabel(c.commandType)));
                      reload();
                    }}
                  />
                </li>
              ))}
            </ol>
          )}
          {commands.length >= limit && limit < most ? (
            <div>
              <Button
                data-testid="log-more"
                onClick={() => {
                  setLimit((l) => Math.min(l + step, most));
                }}
              >
                {ru.admin.showMore}
              </Button>
            </div>
          ) : null}
        </div>
      )}
    </Loading>
  );
}

function LogRow({
  seasonId,
  command,
  all,
  onUndone,
}: {
  seasonId: string;
  command: Command;
  all: readonly Command[];
  onUndone: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [comment, setComment] = useState('');
  const [commentError, setCommentError] = useState<string>();
  const [error, setError] = useState<string>();
  const [dependents, setDependents] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const label = commandLabel(command.commandType);

  async function undo() {
    const problem = commentProblem(comment);
    setCommentError(problem);
    if (problem) return;
    setBusy(true);
    setError(undefined);
    setDependents([]);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/undo', {
        params: { path: { seasonId } },
        body: {
          commandId: newCommandId(),
          targetCommandId: command.commandId,
          comment: comment.trim(),
        },
      });
      if (answer.data) {
        setOpen(false);
        onUndone();
        return;
      }
      // The refusal stays in the dialog: with the later commands that depend on this one
      setError(refusal(answer));
      if (rejectionCode(answer.error) === 'undo.dependents') setDependents(related(answer.error));
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <article
      className="flex flex-wrap items-center gap-x-4 gap-y-1 rounded-md bg-card px-4 py-3"
      data-testid={`command-${command.commandId}`}
    >
      <div className="mr-auto grid min-w-0 gap-1">
        <p className="flex flex-wrap items-center gap-2 font-bold">
          {label}
          {command.undone ? (
            <span className="inline-flex items-center gap-1 rounded-full bg-muted px-2 text-xs font-bold">
              <Undo2 size={12} aria-hidden />
              {t.undone}
            </span>
          ) : null}
        </p>
        <p className="text-sm text-ink-soft">
          {[
            command.authorName ?? ru.admin.system,
            moscowTime(command.occurredAt),
            t.events(command.events.length),
          ].join(', ')}
        </p>
      </div>
      {command.undone || final.has(command.commandType) ? null : (
        <ConfirmDanger
          open={open}
          onOpenChange={(next) => {
            setOpen(next);
            if (!next) {
              setError(undefined);
              setDependents([]);
              setCommentError(undefined);
            }
          }}
          trigger={
            <Button variant="dangerLink" data-testid="undo">
              {t.undo}
            </Button>
          }
          title={t.undoTitle(label)}
          consequences={t.undoConsequences(command.events.length)}
          confirm={t.undoConfirm}
          busy={busy}
          onConfirm={() => void undo()}
        >
          <TextArea
            label={ru.admin.comment}
            hint={t.undoCommentHint}
            rows={2}
            maxLength={500}
            value={comment}
            error={commentError}
            data-testid="undo-comment"
            onChange={(e) => {
              setComment(e.target.value);
            }}
          />
          {error ? <Notice tone="danger">{error}</Notice> : null}
          {dependents.length > 0 ? (
            <div className="grid gap-1" data-testid="undo-dependents">
              <p className="font-bold">{t.dependents}</p>
              <ul className="grid list-disc gap-1 pl-5">
                {dependents.map((id) => {
                  const later = all.find((c) => c.commandId === id);
                  return (
                    <li key={id}>
                      {later
                        ? `${commandLabel(later.commandType)}, ${later.authorName ?? ru.admin.system}, ${moscowTime(later.occurredAt)}`
                        : t.dependentUnknown}
                    </li>
                  );
                })}
              </ul>
            </div>
          ) : null}
        </ConfirmDanger>
      )}
    </article>
  );
}
