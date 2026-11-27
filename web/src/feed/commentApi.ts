import { api, rejectionCode, type Schemas } from '../api/client';

export type CommentTarget =
  | { kind: 'feed'; seasonId: string; targetCommandId: string }
  | { kind: 'review'; seasonId: string; gameId: string; runId: string };
export type ThreadComment = Schemas['ThreadCommentView'];

const commentEventTypes = new Set([
  'comment-posted',
  'comment-edited',
  'comment-deleted',
  'comment-hidden',
]);
export const isCommentEvent = (type: string) => commentEventTypes.has(type);

type ReadResult =
  | { kind: 'ready'; page: Schemas['CommentThreadView'] }
  | { kind: 'signedOut' | 'unavailable' | 'tooOften' | 'failed' };

export async function fetchComments(
  target: CommentTarget,
  before: string | null,
  signal: AbortSignal,
): Promise<ReadResult> {
  try {
    const query = before === null ? {} : { before };
    const result =
      target.kind === 'feed'
        ? await api.GET('/api/seasons/{seasonId}/feed/{targetCommandId}/comments', {
            params: {
              path: { seasonId: target.seasonId, targetCommandId: target.targetCommandId },
              query,
            },
            signal,
          })
        : await api.GET('/api/pool/{gameId}/runs/{runId}/review-comments', {
            params: { path: { gameId: target.gameId, runId: target.runId }, query },
            signal,
          });
    if (result.data) return { kind: 'ready', page: result.data };
    if (result.response.status === 401) return { kind: 'signedOut' };
    if (result.response.status === 404 || rejectionCode(result.error) === 'feature.disabled') {
      return { kind: 'unavailable' };
    }
    if (result.response.status === 429) return { kind: 'tooOften' };
  } catch {
    return { kind: 'failed' };
  }
  return { kind: 'failed' };
}

export type CommentAction =
  | { kind: 'post'; text: string }
  | { kind: 'edit'; id: string; text: string }
  | { kind: 'delete' | 'hide'; id: string };

export type CommentWriteIssue = 'unavailable' | 'tooOften' | 'dailyLimit' | 'forbidden' | 'refused';

type WriteResult =
  | { kind: 'saved' | 'signedOut' }
  | { kind: 'failed'; issue: CommentWriteIssue; uncertain?: boolean };

async function sendComment(
  target: CommentTarget,
  action: CommentAction,
  commandId: string,
  signal: AbortSignal,
) {
  const { seasonId } = target;
  const body = { commandId };
  if (target.kind === 'feed') {
    const path = { seasonId, targetCommandId: target.targetCommandId };
    switch (action.kind) {
      case 'post':
        return api.POST('/api/seasons/{seasonId}/feed/{targetCommandId}/comments', {
          params: { path },
          body: { ...body, commentId: commandId, text: action.text },
          signal,
        });
      case 'edit':
        return api.PUT('/api/seasons/{seasonId}/feed/{targetCommandId}/comments/{commentId}', {
          params: { path: { ...path, commentId: action.id } },
          body: { ...body, text: action.text },
          signal,
        });
      case 'delete':
        return api.DELETE('/api/seasons/{seasonId}/feed/{targetCommandId}/comments/{commentId}', {
          params: { path: { ...path, commentId: action.id }, query: body },
          signal,
        });
      case 'hide':
        return api.POST(
          '/api/admin/seasons/{seasonId}/feed/{targetCommandId}/comments/{commentId}/hide',
          {
            params: { path: { ...path, commentId: action.id } },
            body,
            signal,
          },
        );
    }
  }
  const path = { gameId: target.gameId, runId: target.runId };
  switch (action.kind) {
    case 'post':
      return api.POST('/api/pool/{gameId}/runs/{runId}/review-comments', {
        params: { path },
        body: { ...body, commentId: commandId, text: action.text },
        signal,
      });
    case 'edit':
      return api.PUT('/api/pool/{gameId}/runs/{runId}/review-comments/{commentId}', {
        params: { path: { ...path, commentId: action.id } },
        body: { ...body, text: action.text },
        signal,
      });
    case 'delete':
      return api.DELETE('/api/pool/{gameId}/runs/{runId}/review-comments/{commentId}', {
        params: { path: { ...path, commentId: action.id }, query: body },
        signal,
      });
    case 'hide':
      return api.POST('/api/admin/seasons/{seasonId}/reviews/{runId}/comments/{commentId}/hide', {
        params: { path: { seasonId, runId: target.runId, commentId: action.id } },
        body,
        signal,
      });
  }
}

export async function writeComment(
  target: CommentTarget,
  action: CommentAction,
  commandId: string,
  signal: AbortSignal,
): Promise<WriteResult> {
  try {
    const { response, error } = await sendComment(target, action, commandId, signal);
    const code = rejectionCode(error);
    if (response.status === 204) return { kind: 'saved' };
    if (response.status === 401) return { kind: 'signedOut' };
    if (response.status === 429) return { kind: 'failed', issue: 'tooOften' };
    if (response.status === 403) return { kind: 'failed', issue: 'forbidden' };
    if (response.status === 404 || code === 'feature.disabled') {
      return { kind: 'failed', issue: 'unavailable' };
    }
    if (code === 'comment.dailyLimit') return { kind: 'failed', issue: 'dailyLimit' };
    return { kind: 'failed', issue: 'refused', uncertain: response.status >= 500 };
  } catch {
    return { kind: 'failed', issue: 'refused', uncertain: true };
  }
}
