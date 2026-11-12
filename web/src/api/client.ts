import createClient, { type Middleware } from 'openapi-fetch';
import { recordRequest } from '../app/bugContext';
import { actOnce } from './commands';
import type { components, paths } from './schema';

export type Schemas = components['schemas'];

let csrf: { header: string; token: string } | null = null;

/** Fetches a fresh antiforgery token. Call on start and after signing in or out (the token is bound to the user). */
export async function refreshCsrf(): Promise<void> {
  const { data } = await api.GET('/api/auth/antiforgery');
  csrf = data ? { header: data.headerName, token: data.token } : null;
}

/** The antiforgery header for a request made outside the typed client (a multipart upload), or none. */
export function antiforgeryHeaders(): Record<string, string> {
  return csrf ? { [csrf.header]: csrf.token } : {};
}

const antiforgery: Middleware = {
  onRequest({ request }) {
    if (request.method !== 'GET' && request.method !== 'HEAD' && csrf) {
      request.headers.set(csrf.header, csrf.token);
    }
    return request;
  },
};

/** Fired when a write meets maintenance (503): the banner asks the site at once instead of waiting for its next look. */
export const MAINTENANCE_EVENT = 'site:maintenance';

/**
 * Fired when the server no longer knows this sign-in (401 on anything but the sign-in itself): the session ended on
 * another device or by the admin (D-466), and the page goes to the sign-in form at once.
 */
export const SIGNED_OUT_EVENT = 'site:signedOut';

/**
 * Every answer goes into the bug report's context; a 503 tells the banner the site only reads now (D-121), a 401 tells
 * the page its session is over.
 */
export function noteResponse(method: string, url: string, status: number) {
  recordRequest(method, url, status);
  if (status === 503) globalThis.dispatchEvent(new Event(MAINTENANCE_EVENT));
  if (
    status === 401 &&
    !new URL(url, globalThis.location.origin).pathname.startsWith('/api/auth/login')
  )
    globalThis.dispatchEvent(new Event(SIGNED_OUT_EVENT));
}

const observed: Middleware = {
  onResponse({ request, response }) {
    noteResponse(request.method, request.url, response.status);
    return response;
  },
};

/** Typed client of the backend; types are generated from OpenAPI (`npm run gen:api`), never written by hand. */
export const api = createClient<paths>({
  baseUrl: globalThis.location.origin,
  credentials: 'same-origin',
  // Looked up per call, so tests can stub the global fetch.
  fetch: (request) => globalThis.fetch(request),
});
api.use(antiforgery, observed, actOnce);

/** Engine rejection code from a 409 answer, or null. */
export function rejectionCode(error: unknown): string | null {
  if (error && typeof error === 'object' && 'code' in error && typeof error.code === 'string') {
    return error.code;
  }
  return null;
}
