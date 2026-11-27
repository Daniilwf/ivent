import type { Schemas } from '../api/client';

export function isOpenPoll(poll: Schemas['PollView']) {
  return poll.status === 'open' && poll.voting?.reason !== 'season.closed';
}
