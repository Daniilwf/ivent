import { api, type Schemas } from '../api/client';
import { answerOf, type Answer } from '../app/useLoaded';

/** How many events a page asks for: a busy evening's worth */
export const FEED_PAGE = 50;

/** One page of a season's feed: the newest, or the one before a sequence */
export async function fetchFeedPage(
  seasonId: string,
  before: number | null,
  limit = FEED_PAGE,
): Promise<Answer<Schemas['FeedView']>> {
  try {
    return answerOf(
      await api.GET('/api/seasons/{seasonId}/feed', {
        params: { path: { seasonId }, query: before === null ? { limit } : { before, limit } },
      }),
    );
  } catch {
    return { kind: 'failed' };
  }
}
