import type { Middleware } from 'openapi-fetch';

/** A command id for one action (D-68); a retry of the same request keeps the first one's id (actOnce) */
export const newCommandId = () => crypto.randomUUID();

/**
 * How long a request without an answer may be retried as the same command. A retry is the person pressing again
 * after an error, seconds later; a press long after is a new action even with the same body (a drop has none).
 */
export const retryWindowMs = 60_000;

// D-68 for every command of the site (one mechanism, D-202): a request the server has not answered (the answer was
// lost, the connection dropped) and that is sent again unchanged within the retry window carries the first attempt's
// command id, so the command acts once. The request is the same when its method, address and body without the command
// id are. Any answer of the server to the same method and address — whatever its status and body — ends every attempt
// there: the next request is a new command.
type Attempt = { commandId: string; at: number };
const unanswered = new Map<string, Attempt>();
const keys = new Map<string, { key: string; target: string }>();

const targetOf = (request: Request) => `${request.method} ${request.url}`;

function forget(target: string) {
  for (const key of unanswered.keys()) if (key.startsWith(`${target} `)) unanswered.delete(key);
}

export const actOnce: Middleware = {
  async onRequest({ request, id }) {
    if (request.method === 'GET' || !new URL(request.url).pathname.startsWith('/api/'))
      return undefined;
    let body: unknown;
    try {
      body = JSON.parse(await request.clone().text());
    } catch {
      return undefined;
    }
    if (!body || typeof body !== 'object' || Array.isArray(body)) return undefined;
    const { commandId, ...rest } = body as Record<string, unknown>;
    if (typeof commandId !== 'string') return undefined;
    const target = targetOf(request);
    const key = `${target} ${JSON.stringify(rest)}`;
    keys.set(id, { key, target });
    const now = Date.now();
    const first = unanswered.get(key);
    if (!first || now - first.at > retryWindowMs) {
      unanswered.set(key, { commandId, at: now });
      return undefined;
    }
    return new Request(request, { body: JSON.stringify({ ...rest, commandId: first.commandId }) });
  },
  onResponse({ id }) {
    const sent = keys.get(id);
    keys.delete(id);
    if (sent) forget(sent.target);
    return undefined;
  },
  onError({ id }) {
    keys.delete(id);
    return undefined;
  },
};
