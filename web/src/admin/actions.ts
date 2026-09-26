import type { Middleware } from 'openapi-fetch';
import { api, rejectionCode } from '../api/client';
import { ru } from '../i18n/ru';

const t = ru.admin;

type Answer = { error?: unknown; response: Response };

/** What to tell the admin when an action was refused: the engine's reason, a bad field, no rights, maintenance */
export function refusal(answer: Answer): string {
  const code = rejectionCode(answer.error);
  if (code) return t.rejection[code] ?? ru.rejection[code] ?? ru.rejection.unknown;
  const status = answer.response.status;
  if (status === 400) return t.invalid;
  if (status === 401 || status === 403) return t.forbidden;
  if (status === 404) return t.notFound;
  if (status === 503) return ru.rejection['site.maintenance'];
  return t.failed;
}

/** Ids the engine listed with a refusal (undo.dependents: the later commands that depend on the undone one) */
export function related(error: unknown): string[] {
  if (error && typeof error === 'object' && 'related' in error && Array.isArray(error.related))
    return error.related.filter((id): id is string => typeof id === 'string');
  return [];
}

/** A command id for one action; a retry of the same request keeps the first one's id (actOnce, D-68) */
export const newCommandId = () => crypto.randomUUID();

// D-68 for the admin's actions: a request the server has not answered yet (the answer was lost, the connection
// dropped) and that is sent again unchanged carries the first attempt's command id, so the command acts once. The
// request is the same when its method, address and body without the command id are. Any answer of the server ends
// it: the next request gets its own id.
const unanswered = new Map<string, string>();
const keys = new Map<string, string>();

export const actOnce: Middleware = {
  async onRequest({ request, id }) {
    if (request.method === 'GET' || !new URL(request.url).pathname.startsWith('/api/admin/'))
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
api.use(actOnce);

/** The comment every admin correction writes into the log: required, at most 500 characters */
export function commentProblem(comment: string): string | undefined {
  if (comment.trim() === '') return t.commentRequired;
  if (comment.length > 500) return t.commentTooLong;
  return undefined;
}

/** A whole number typed by a person: «−3» with a real minus sign too; empty is zero */
export function parseWhole(text: string): number | null {
  const clean = text.trim().replace('−', '-');
  if (clean === '') return 0;
  return /^[-+]?\d+$/.test(clean) ? Number(clean) : null;
}

/** A time typed in a «datetime-local» field is Moscow time (the site's clock, SH4) */
export function moscowInput(value: string): string {
  return `${value}:00+03:00`;
}

/** A command of the season log by its meaning; an unknown type as it is */
export const commandLabel = (type: string) => t.log.commands[type] ?? type;
