// Tests only: a fake backend for the admin's pages. Routes are «METHOD /path» with «*» for one path segment; a route
// answers with a body (200) or a function of the request that returns { status, body }. Every request is recorded.

export type Call = { method: string; path: string; query: URLSearchParams; body: unknown };
type Answer = { status?: number; body?: unknown };
// A body, or a function of the request that returns an Answer
type Route = unknown;

export function answer(status: number, body?: unknown): (call: Call) => Answer {
  return () => ({ status, body });
}

export function fakeServer(routes: Record<string, Route>) {
  const calls: Call[] = [];
  const compiled = Object.entries(routes).map(([key, route]) => {
    const [method, pattern] = key.split(' ') as [string, string];
    const regex = new RegExp(`^${pattern.replaceAll('*', '[^/]+')}$`);
    return { method, regex, route };
  });

  const fetch = vi.fn(async (input: Request) => {
    const url = new URL(input.url);
    const text = input.method === 'GET' ? '' : await input.clone().text();
    const call: Call = {
      method: input.method,
      path: url.pathname,
      query: url.searchParams,
      body: text === '' ? null : (JSON.parse(text) as unknown),
    };
    calls.push(call);
    const found = compiled.find((r) => r.method === input.method && r.regex.test(url.pathname));
    if (!found) return new Response(null, { status: 404 });
    const result: Answer =
      typeof found.route === 'function'
        ? (found.route as (call: Call) => Answer)(call)
        : { body: found.route };
    const status = result.status ?? 200;
    return new Response(result.body === undefined ? null : JSON.stringify(result.body), {
      status,
      headers: {
        'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
      },
    });
  });
  vi.stubGlobal('fetch', fetch);

  return {
    calls,
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
