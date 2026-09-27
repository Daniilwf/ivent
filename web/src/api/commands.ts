import type { Middleware } from 'openapi-fetch';

/** A command id for one action (D-68); a retry of the same request keeps the first one's id (actOnce) */
export const newCommandId = () => crypto.randomUUID();

// D-68 for every command of the site (one mechanism, D-202): a request the server has not answered yet (the answer was
// lost, the connection dropped) and that is sent again unchanged carries the first attempt's command id, so the command
// acts once. The request is the same when its method, address and body without the command id are. Any answer of the
// server ends it: the next request gets its own id.
const unanswered = new Map<string, string>();
const keys = new Map<string, string>();

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
    const key = `${request.method} ${request.url} ${JSON.stringify(rest)}`;
    keys.set(id, key);
    const first = unanswered.get(key);
    if (!first) {
      unanswered.set(key, commandId);
      return undefined;
    }
    return new Request(request, { body: JSON.stringify({ ...rest, commandId: first }) });
  },
  onResponse({ id }) {
    const key = keys.get(id);
    keys.delete(id);
    if (key) unanswered.delete(key);
    return undefined;
  },
  onError({ id }) {
    keys.delete(id);
    return undefined;
  },
};
