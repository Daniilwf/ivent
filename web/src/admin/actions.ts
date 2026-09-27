import { rejectionCode } from '../api/client';
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

/** A command of the season log by its meaning; an unknown type as it is */
export const commandLabel = (type: string) => t.log.commands[type] ?? type;
