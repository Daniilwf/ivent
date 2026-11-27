import { api, type Schemas } from '../api/client';
import { newCommandId } from '../api/commands';
import { answerOf, type Answer } from '../app/useLoaded';

/** How many events a page asks for: a busy evening's worth */
export const FEED_PAGE = 50;
export const FEED_REACTION_BATCH = 100;
export type FeedReactionMap = NonNullable<Schemas['FeedView']['reactions']>;

/** One page of a season's feed: the newest, or the one before a sequence */
export async function fetchFeedPage(
  seasonId: string,
  before: number | null,
  limit = FEED_PAGE,
  signal?: AbortSignal,
): Promise<Answer<Schemas['FeedView']>> {
  try {
    return answerOf(
      await api.GET('/api/seasons/{seasonId}/feed', {
        params: { path: { seasonId }, query: before === null ? { limit } : { before, limit } },
        ...(signal && { signal }),
      }),
    );
  } catch {
    return { kind: 'failed' };
  }
}

export async function fetchFeedReactions(
  seasonId: string,
  commandIds: string[],
  signal: AbortSignal,
): Promise<Answer<FeedReactionMap>> {
  try {
    return answerOf(
      await api.GET('/api/seasons/{seasonId}/feed/reactions', {
        params: { path: { seasonId }, query: { commandIds } },
        querySerializer: { array: { style: 'form', explode: true } },
        signal,
      }),
    );
  } catch {
    return { kind: 'failed' };
  }
}

export async function setFeedReaction(
  seasonId: string,
  targetCommandId: string,
  emoji: string,
  selected: boolean,
  signal: AbortSignal,
): Promise<'saved' | 'signedOut' | 'tooOften' | 'refused'> {
  try {
    const { response } = await api.PUT('/api/seasons/{seasonId}/feed/{targetCommandId}/reactions', {
      params: { path: { seasonId, targetCommandId } },
      body: { commandId: newCommandId(), emoji, selected },
      signal,
    });
    if (response.status === 204) return 'saved';
    if (response.status === 401) return 'signedOut';
    if (response.status === 429) return 'tooOften';
  } catch {
    return 'refused';
  }
  return 'refused';
}
