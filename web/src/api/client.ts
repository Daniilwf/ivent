import createClient, { type Middleware } from 'openapi-fetch';
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

/** Typed client of the backend; types are generated from OpenAPI (`npm run gen:api`), never written by hand. */
export const api = createClient<paths>({
  baseUrl: globalThis.location.origin,
  credentials: 'same-origin',
  // Looked up per call, so tests can stub the global fetch.
  fetch: (request) => globalThis.fetch(request),
});
api.use(antiforgery);

/** Engine rejection code from a 409 answer, or null. */
export function rejectionCode(error: unknown): string | null {
  if (error && typeof error === 'object' && 'code' in error && typeof error.code === 'string') {
    return error.code;
  }
  return null;
}
