import { useCallback, useEffect, useId, useMemo, useRef, useState, type Ref } from 'react';
import { newCommandId } from '../api/commands';
import { moscowTime } from '../app/time';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { SummaryButton } from '../ui/Disclosure';
import { ConfirmDanger } from '../ui/Dialogs';
import { TextArea } from '../ui/Field';
import { Skeleton } from '../ui/Progress';
import { Notice } from '../ui/States';
import {
  fetchComments,
  isCommentEvent,
  writeComment,
  type CommentAction,
  type CommentTarget,
  type CommentWriteIssue,
  type ThreadComment,
} from './commentApi';

const t = ru.comments;
const maxCommentLength = 500;
const refreshIntervalMs = 5000;

type Window = {
  comments: ThreadComment[];
  nextBefore: string | null;
  pages: number;
  postingAllowed: boolean;
};
type ReadIssue = 'unavailable' | 'tooOften' | 'readFailed';
type PendingRead = { controller: AbortController; older: boolean };
type Removal = Extract<CommentAction, { kind: 'delete' | 'hide' }>;
type EditDraft = Pick<ThreadComment, 'id' | 'createdAt' | 'text'>;

function uniqueComments(items: ThreadComment[]): ThreadComment[] {
  return [...new Map(items.map((item) => [item.id, item])).values()];
}

function reconcileEditor(draft: EditDraft | null, next: Window): EditDraft | null {
  if (!draft) return null;
  const comment = next.comments.find((entry) => entry.id === draft.id);
  if (comment) return comment.canEdit ? draft : null;
  const oldest = next.comments[0];
  // Compare the same UTC timestamp and id used by the server's cursor.
  const covered =
    next.nextBefore === null ||
    (oldest &&
      (oldest.createdAt < draft.createdAt ||
        (oldest.createdAt === draft.createdAt && oldest.id < draft.id)));
  return covered ? null : draft;
}

export function CommentThread({
  target: thread,
  label,
  onSignedOut,
}: {
  target: CommentTarget;
  label: string;
  onSignedOut: () => void;
}) {
  const { seasonId } = thread;
  const targetId = thread.kind === 'feed' ? thread.targetCommandId : thread.runId;
  const gameId = thread.kind === 'review' ? thread.gameId : null;
  const target = useMemo<CommentTarget>(
    () =>
      gameId === null
        ? { kind: 'feed', seasonId, targetCommandId: targetId }
        : { kind: 'review', seasonId, gameId, runId: targetId },
    [gameId, seasonId, targetId],
  );
  const id = useId();
  const [open, setOpen] = useState(false);
  const opening = useRef(false);
  const [draft, setDraft] = useState('');
  const [editing, setEditing] = useState<EditDraft | null>(null);
  const [removal, setRemoval] = useState<Removal | null>(null);
  const [pending, setPending] = useState<CommentAction['kind'] | null>(null);
  const [writeIssue, setWriteIssue] = useState<CommentWriteIssue | null>(null);
  const [readIssue, setReadIssue] = useState<ReadIssue | null>(null);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [window, setWindow] = useState<Window | null>(null);
  const current = useRef<Window | null>(null);
  const lifetime = useRef<AbortController | null>(null);
  const reader = useRef<PendingRead | null>(null);
  const olderQueued = useRef(false);
  const writing = useRef(false);
  const attempts = useRef(new Map<string, string>());
  const composer = useRef<HTMLTextAreaElement>(null);
  const editor = useRef<HTMLTextAreaElement>(null);
  const focusEditorOnMount = useRef(false);
  const actionTrigger = useRef<HTMLButtonElement>(null);
  const toggle = useRef<HTMLButtonElement>(null);
  const olderButton = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    lifetime.current = controller;
    return () => {
      controller.abort();
      reader.current?.controller.abort();
      reader.current = null;
    };
  }, []);

  const show = useCallback((next: Window | null) => {
    if (!next?.postingAllowed) {
      if (composer.current === document.activeElement) toggle.current?.focus();
      if (next) setWriteIssue((issue) => (issue === 'dailyLimit' ? null : issue));
    }
    if (!next?.nextBefore && olderButton.current === document.activeElement) {
      (next?.postingAllowed ? composer.current : toggle.current)?.focus();
    }
    current.current = next;
    setWindow(next);
  }, []);

  const attachEditor = useCallback((input: HTMLTextAreaElement | null) => {
    if (input && focusEditorOnMount.current) {
      input.focus();
      focusEditorOnMount.current = false;
    } else if (!input && editor.current === document.activeElement) {
      focusEditorOnMount.current = true;
      (composer.current ?? toggle.current)?.focus();
    }
    editor.current = input;
  }, []);

  const read = useCallback(
    async function readWindow(older = false) {
      if (!opening.current) return;
      const pendingRead = reader.current;
      if (older && pendingRead) {
        if (!pendingRead.older) olderQueued.current = true;
        return;
      }
      const previous = current.current;
      let before = older ? (previous?.nextBefore ?? null) : null;
      if (older && before === null) return;
      const controller = new AbortController();
      // The head must reconcile moderation before an older page can append to that snapshot.
      if (pendingRead?.older) olderQueued.current = true;
      pendingRead?.controller.abort();
      reader.current = { controller, older };
      const isCurrentRead = () => reader.current?.controller === controller;
      const wanted = older ? 1 : (previous?.pages ?? 1);
      let comments: ThreadComment[] = [];
      let pages = 0;
      let postingAllowed = previous?.postingAllowed ?? true;
      let complete = false;
      try {
        for (let pageIndex = 0; pageIndex < wanted; pageIndex++) {
          const result = await fetchComments(target, before, controller.signal);
          if (controller.signal.aborted) return;
          if (result.kind !== 'ready') {
            if (result.kind === 'signedOut') {
              onSignedOut();
              return;
            }
            if (result.kind === 'unavailable' && before === null) {
              show(null);
              setReadIssue('unavailable');
              return;
            }
            // Keep the portion already verified if an older cursor expired during a head refresh.
            if (pages > 0)
              show({
                comments: uniqueComments(comments),
                nextBefore: before,
                pages,
                postingAllowed,
              });
            setReadIssue(result.kind === 'tooOften' ? 'tooOften' : 'readFailed');
            return;
          }
          comments = [...result.page.comments, ...comments];
          before = result.page.nextBefore;
          postingAllowed = result.page.postingAllowed ?? true;
          pages++;
          if (before === null) break;
        }
        if (older && previous) {
          comments = [...comments, ...previous.comments];
          pages += previous.pages;
        }
        const next = {
          comments: uniqueComments(comments),
          nextBefore: before,
          pages,
          postingAllowed,
        };
        show(next);
        setReadIssue(null);
        setEditing((draft) => reconcileEditor(draft, next));
        complete = true;
      } finally {
        if (isCurrentRead()) {
          const loadOlder = olderQueued.current && complete && !older && before !== null;
          reader.current = null;
          olderQueued.current = false;
          if (loadOlder) void readWindow(true);
          else setLoadingOlder(false);
        }
      }
    },
    [target, onSignedOut, show],
  );

  const version = useSeasonVersion(open ? target.seasonId : null, {
    relevant: ({ types, commentTarget }) => {
      if (types.includes('command-undone') || types.includes('ruleset-changed')) return true;
      return (
        types.some(isCommentEvent) &&
        (!commentTarget?.id ||
          !commentTarget.kind ||
          (commentTarget.kind === (target.kind === 'feed' ? 'feedEntry' : 'review') &&
            commentTarget.id.toLowerCase() === targetId.toLowerCase()))
      );
    },
    refreshOnJoin: true,
    throttleMs: refreshIntervalMs,
  });
  useEffect(() => {
    if (open) void read();
  }, [open, read, version]);

  function focusAfterUpdate() {
    const ref = actionTrigger.current;
    requestAnimationFrame(() => {
      if (!opening.current) return;
      if (ref?.isConnected) ref.focus();
      else (composer.current ?? toggle.current)?.focus();
    });
  }

  async function mutate(action: CommentAction) {
    const signal = lifetime.current?.signal;
    if (!signal || writing.current) return;
    writing.current = true;
    setPending(action.kind);
    setWriteIssue(null);
    const intent = JSON.stringify(action);
    const commandId = attempts.current.get(intent) ?? newCommandId();
    const result = await writeComment(target, action, commandId, signal);
    if (signal.aborted) return;
    writing.current = false;
    setPending(null);
    // A 429 says nothing about whether an earlier attempt made it through the queue.
    if (result.kind === 'failed' && result.uncertain) attempts.current.set(intent, commandId);
    else if (result.kind !== 'failed' || result.issue !== 'tooOften')
      attempts.current.delete(intent);
    if (result.kind === 'signedOut') {
      onSignedOut();
      return;
    }
    if (result.kind === 'failed') {
      setWriteIssue(result.issue);
      if (result.issue === 'dailyLimit') void read();
      return;
    }

    if (action.kind === 'post') {
      setDraft('');
      actionTrigger.current = null;
    } else if (current.current) {
      const shown = current.current;
      show({
        ...shown,
        comments:
          action.kind === 'edit'
            ? shown.comments.map((comment) =>
                comment.id === action.id ? { ...comment, text: action.text } : comment,
              )
            : shown.comments.filter((comment) => comment.id !== action.id),
      });
    }
    if (action.kind === 'edit') setEditing(null);
    if (action.kind === 'hide' || action.kind === 'delete') setRemoval(null);
    focusAfterUpdate();
    void read();
  }

  const readMessage = readIssue && t[readIssue];
  const writeMessage = writeIssue && t[writeIssue];
  const editingForm = editing ? (
    <CommentForm
      label={t.editLabel}
      text={editing.text}
      onChange={(text) => {
        setEditing({ ...editing, text });
      }}
      onSubmit={(text) => void mutate({ kind: 'edit', id: editing.id, text })}
      onCancel={() => {
        setEditing(null);
        setWriteIssue(null);
        focusAfterUpdate();
      }}
      inputRef={attachEditor}
      submit={t.save}
      busy={pending === 'edit'}
      disabled={pending !== null}
      error={!removal ? writeMessage : null}
    />
  ) : null;
  const editorHasRow = window?.comments.some((entry) => entry.canEdit && entry.id === editing?.id);
  return (
    <div className="grid min-w-0">
      <SummaryButton
        ref={toggle}
        id={`${id}-label`}
        open={open}
        aria-controls={id}
        aria-label={label}
        className="justify-self-start text-sm font-bold"
        onClick={() => {
          opening.current = !open;
          setOpen(!open);
          if (open) {
            reader.current?.controller.abort();
            reader.current = null;
            olderQueued.current = false;
            setLoadingOlder(false);
            setReadIssue(null);
            show(null);
          }
        }}
      >
        {t.title}
      </SummaryButton>
      <div id={id} role="region" aria-labelledby={`${id}-label`} hidden={!open}>
        {open ? (
          <div className="grid min-w-0 gap-3 rounded-md border-2 border-muted p-3">
            {!window && !readIssue ? (
              <div className="grid gap-2" aria-busy="true" aria-label={ru.ui.loading}>
                <Skeleton className="h-4 w-1/3" />
                <Skeleton className="h-4 w-2/3" />
              </div>
            ) : null}
            {readMessage ? (
              <div className="grid justify-items-start gap-1">
                <Notice tone="danger">{readMessage}</Notice>
                {readIssue !== 'unavailable' ? (
                  <Button
                    variant="link"
                    onClick={() => {
                      setReadIssue(null);
                      void read();
                    }}
                  >
                    {ru.ui.retry}
                  </Button>
                ) : null}
              </div>
            ) : null}
            {window?.nextBefore ? (
              <Button
                ref={olderButton}
                variant="link"
                className="justify-self-start whitespace-normal text-left"
                loading={loadingOlder}
                onClick={() => {
                  if (!current.current?.nextBefore) return;
                  setLoadingOlder(true);
                  void read(true);
                }}
              >
                {t.older}
              </Button>
            ) : null}
            {window?.comments.length ? (
              <ol className="grid min-w-0 divide-y-2 divide-muted">
                {window.comments.map((comment) => (
                  <li key={comment.id} className="grid min-w-0 gap-1 py-2">
                    <header className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                      <strong className="wrap-anywhere">{comment.author}</strong>
                      <time dateTime={comment.createdAt} className="text-sm text-ink-soft">
                        {moscowTime(comment.createdAt)}
                      </time>
                      {comment.editedAt ? (
                        <span
                          className="text-sm text-ink-soft"
                          title={moscowTime(comment.editedAt)}
                        >
                          {t.edited}
                        </span>
                      ) : null}
                    </header>
                    {editing?.id === comment.id && comment.canEdit ? (
                      editingForm
                    ) : (
                      <p className="min-w-0 wrap-anywhere whitespace-pre-wrap">{comment.text}</p>
                    )}
                    {comment.canEdit || comment.canHide ? (
                      <div className="flex flex-wrap gap-x-3">
                        {comment.canEdit ? (
                          <>
                            <Button
                              variant="link"
                              disabled={pending !== null || editing !== null}
                              onClick={(event) => {
                                actionTrigger.current = event.currentTarget;
                                setWriteIssue(null);
                                focusEditorOnMount.current = true;
                                setEditing(comment);
                              }}
                            >
                              {t.edit}
                            </Button>
                            <Button
                              variant="dangerLink"
                              disabled={pending !== null || editing !== null}
                              onClick={(event) => {
                                actionTrigger.current = event.currentTarget;
                                setWriteIssue(null);
                                setRemoval({ kind: 'delete', id: comment.id });
                              }}
                            >
                              {t.delete}
                            </Button>
                          </>
                        ) : null}
                        {comment.canHide ? (
                          <Button
                            variant="dangerLink"
                            disabled={pending !== null || editing !== null}
                            onClick={(event) => {
                              actionTrigger.current = event.currentTarget;
                              setWriteIssue(null);
                              setRemoval({ kind: 'hide', id: comment.id });
                            }}
                          >
                            {t.hide}
                          </Button>
                        ) : null}
                      </div>
                    ) : null}
                  </li>
                ))}
              </ol>
            ) : window ? (
              <p className="text-sm text-ink-soft">{t.empty}</p>
            ) : null}
            {window && !editorHasRow ? editingForm : null}
            {window?.postingAllowed ? (
              <CommentForm
                label={t.write}
                inputRef={composer}
                text={draft}
                onChange={setDraft}
                onSubmit={(text) => void mutate({ kind: 'post', text })}
                submit={t.send}
                busy={pending === 'post'}
                disabled={pending !== null || editing !== null}
                error={!editing && !removal ? writeMessage : null}
              />
            ) : window ? (
              <p className="text-sm text-ink-soft" role="status">
                {t.postingDisabled}
              </p>
            ) : null}
          </div>
        ) : null}
      </div>
      {removal ? (
        <ConfirmDanger
          open
          title={removal.kind === 'delete' ? t.deleteTitle : t.hideTitle}
          consequences={[removal.kind === 'delete' ? t.deleteNote : t.hideNote]}
          confirm={t[removal.kind]}
          busy={pending === removal.kind}
          onConfirm={() => void mutate(removal)}
          onOpenChange={(next) => {
            if (!next && !writing.current) {
              setRemoval(null);
              setWriteIssue(null);
              focusAfterUpdate();
            }
          }}
        >
          {writeMessage ? <Notice tone="danger">{writeMessage}</Notice> : null}
        </ConfirmDanger>
      ) : null}
    </div>
  );
}

function CommentForm({
  label,
  text,
  onChange,
  onSubmit,
  onCancel,
  inputRef,
  submit,
  busy,
  disabled,
  error,
}: {
  label: string;
  text: string;
  onChange: (value: string) => void;
  onSubmit: (value: string) => void;
  onCancel?: () => void;
  inputRef: Ref<HTMLTextAreaElement>;
  submit: string;
  busy: boolean;
  disabled: boolean;
  error: string | null;
}) {
  const trimmed = text.trim();
  return (
    <form
      className="grid min-w-0 gap-2"
      onSubmit={(event) => {
        event.preventDefault();
        if (!disabled && trimmed) onSubmit(trimmed);
      }}
    >
      <TextArea
        label={label}
        value={text}
        onChange={(event) => {
          onChange(event.target.value);
        }}
        readOnly={busy}
        maxLength={maxCommentLength}
        ref={inputRef}
        className="min-w-0"
        {...(error ? { error } : {})}
      />
      <div className="flex flex-wrap gap-2">
        <Button
          type="submit"
          variant="quiet"
          loading={busy}
          disabled={!trimmed || (disabled && !busy)}
        >
          {submit}
        </Button>
        {onCancel ? (
          <Button variant="link" disabled={busy} onClick={onCancel}>
            {ru.ui.cancel}
          </Button>
        ) : null}
      </div>
    </form>
  );
}
