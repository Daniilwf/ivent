import { api, type Schemas } from '../api/client';

/** How many events a page asks for: a busy evening's worth */
export const FEED_PAGE = 50;

export type FetchedPage =
  | { kind: 'page'; page: Schemas['FeedView'] }
  | { kind: 'signedOut' }
  | { kind: 'notFound' }
  | { kind: 'failed' };

/** One page of a season's feed: the newest, or the one before a sequence */
export async function fetchFeedPage(
  seasonId: string,
  before: number | null,
  limit = FEED_PAGE,
): Promise<FetchedPage> {
  try {
    const { data, response } = await api.GET('/api/seasons/{seasonId}/feed', {
      params: { path: { seasonId }, query: before === null ? { limit } : { before, limit } },
    });
    if (data) return { kind: 'page', page: data };
    if (response.status === 401) return { kind: 'signedOut' };
    if (response.status === 404) return { kind: 'notFound' };
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}
