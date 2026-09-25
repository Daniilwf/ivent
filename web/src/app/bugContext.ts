import type { Schemas } from '../api/client';

// The context of a bug report (D-121, GLOSSARY «Контекст отчёта»): the page keeps its last actions and the browser's
// errors in memory, so the «Сообщить о баге» button sends them along. What the user types into fields is never kept.

type Entry = Schemas['BugContextEntryView'];

export const MAX_ACTIONS = 30;
export const MAX_ERRORS = 20;
export const MAX_TEXT = 300;

const actions: Entry[] = [];
const errors: Entry[] = [];

const ids = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;

function push(list: Entry[], text: string, max: number) {
  list.push({ at: new Date().toISOString(), text: text.slice(0, MAX_TEXT) });
  if (list.length > max) list.splice(0, list.length - max);
}

export function recordAction(text: string) {
  push(actions, text, MAX_ACTIONS);
}

export function recordError(text: string) {
  push(errors, text, MAX_ERRORS);
}

/** A request as the context keeps it: the method, the path without ids and the answer's status. */
export function recordRequest(method: string, url: string, status: number) {
  const path = new URL(url, globalThis.location.origin).pathname.replace(ids, '…');
  recordAction(`${method} ${path} → ${status}`);
}

/** What was clicked: the element, its test id and its visible label — never a field's value. */
export function describe(element: Element): string {
  const target =
    element.closest('button, a, [role="button"], input, select, textarea, label') ?? element;
  const tag = target.tagName.toLowerCase();
  const testId = target.getAttribute('data-testid');
  const typing = tag === 'input' || tag === 'textarea' || tag === 'select';
  const label = typing
    ? (target.getAttribute('name') ?? target.getAttribute('aria-label') ?? '')
    : (target.getAttribute('aria-label') ?? target.textContent).replace(/\s+/g, ' ').trim();
  return `click ${tag}${testId ? `[data-testid=${testId}]` : ''}${label ? ` «${label.slice(0, 40)}»` : ''}`;
}

function message(reason: unknown): string {
  if (reason instanceof Error) return `${reason.name}: ${reason.message}`;
  if (typeof reason === 'string') return reason;
  // JSON.stringify gives undefined for undefined and functions, whatever its type says
  const json = JSON.stringify(reason) as string | undefined;
  return json ?? String(reason);
}

/** Starts keeping the context; the answer stops it (tests start and stop it around each case). */
export function startBugContext(): () => void {
  const onClick = (event: MouseEvent) => {
    if (event.target instanceof Element) recordAction(describe(event.target));
  };
  const onError = (event: ErrorEvent) => {
    recordError(event.error instanceof Error ? message(event.error) : event.message);
  };
  const onRejection = (event: PromiseRejectionEvent) => {
    recordError(`Unhandled: ${message(event.reason)}`);
  };
  const consoleError = console.error;
  console.error = (...args: unknown[]) => {
    recordError(args.map(message).join(' '));
    consoleError(...args);
  };

  document.addEventListener('click', onClick, true);
  globalThis.addEventListener('error', onError);
  globalThis.addEventListener('unhandledrejection', onRejection);
  return () => {
    document.removeEventListener('click', onClick, true);
    globalThis.removeEventListener('error', onError);
    globalThis.removeEventListener('unhandledrejection', onRejection);
    console.error = consoleError;
  };
}

/** The context as it goes into a report now. */
export function bugContext(): Schemas['BugContextView'] {
  return {
    userAgent: navigator.userAgent.slice(0, 500),
    viewport: `${globalThis.innerWidth}x${globalThis.innerHeight}`,
    actions: [...actions],
    errors: [...errors],
  };
}

/** Forgets everything kept (for tests). */
export function clearBugContext() {
  actions.length = 0;
  errors.length = 0;
}
