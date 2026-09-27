// Tests only: one fake backend for every page's tests (D-202). Routes are «METHOD /path» (or «/path» for any method)
// with «*» for one path segment; a route answers with a body (200), a function of the request that returns
// { status, body }, or a function that returns a Response as it is. The antiforgery token is answered unless a test
// gives its own. Every request is recorded.

export type Call = { method: string; path: string; query: URLSearchParams; body: unknown };
type Answer = { status?: number; body?: unknown };
// A body, or a function of the request that returns an Answer or a Response
type Route = unknown;

/** A JSON answer of the backend: an error status is a problem, no body is an empty answer */
export function json(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

export function answer(status: number, body?: unknown): (call: Call) => Answer {
  return () => ({ status, body });
}

// A path is literal but for «*»: the other signs of a regular expression are escaped
/** A request's body: JSON, the text itself when it is not JSON (a form), or none */
function parseBody(text: string): unknown {
  if (text === '') return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return text;
  }
}

const escape = (text: string) => text.replace(/[.+?^${}()|[\]\\]/g, '\\$&');

export function fakeServer(routes: Record<string, Route>) {
  const calls: Call[] = [];
  const compiled = Object.entries({
    'GET /api/auth/antiforgery': { token: 't', headerName: 'X-CSRF-TOKEN' },
    ...routes,
  }).map(([key, route]) => {
    const [method, pattern] = key.startsWith('/')
      ? [null, key]
      : (key.split(' ') as [string, string]);
    const regex = new RegExp(`^${escape(pattern).replaceAll('*', '[^/]+')}$`);
    return { method, regex, route };
  });

  const fetch = vi.fn(async (input: Request) => {
    const url = new URL(input.url);
    const text = input.method === 'GET' ? '' : await input.clone().text();
    const body = parseBody(text);
    const call: Call = { method: input.method, path: url.pathname, query: url.searchParams, body };
    calls.push(call);
    // A test's own route wins over the default antiforgery answer: the last match counts
    const found = compiled.findLast(
      (r) => (r.method === null || r.method === input.method) && r.regex.test(url.pathname),
    );
    if (!found) return json(404);
    if (typeof found.route === 'function') {
      const result = await (found.route as (call: Call) => Answer | Response | Promise<Response>)(
        call,
      );
      if (result instanceof Response) return result;
      return json(result.status ?? 200, result.body);
    }
    return json(200, found.route);
  });
  vi.stubGlobal('fetch', fetch);

  return {
    calls,
    fetch,
    /** The requests sent to a route, by method and a path ending */
    sent: (method: string, ending: string) =>
      calls.filter((c) => c.method === method && c.path.endsWith(ending)),
  };
}

export const seasonId = '5ea50000-0000-0000-0000-000000000001';

export const adminUser = {
  id: 'a0000000-0000-0000-0000-000000000001',
  login: 'admin',
  name: 'Админ',
  role: 'admin' as const,
  mustChangePassword: false,
  avatar: null,
};
