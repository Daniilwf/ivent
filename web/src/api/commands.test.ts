import { fakeServer, json, type Call } from '../test/fakeServer';
import { api } from './client';
import { actOnce, newCommandId, retryWindowMs } from './commands';

// D-68, D-202: a retry of a request the server did not answer is the same command; nothing else is.

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const idOf = (call: Call | undefined) => (call?.body as { commandId?: string } | null)?.commandId;

function drop() {
  return api.POST('/api/seasons/{seasonId}/drop', {
    params: { path: { seasonId } },
    body: { commandId: newCommandId() },
  });
}

/** The first answers are lost (the connection drops), the rest are this status */
function serve(lost: number, status = 200) {
  let left = lost;
  return fakeServer({
    [`POST /api/seasons/${seasonId}/drop`]: () => {
      if (left > 0) {
        left -= 1;
        throw new TypeError('Failed to fetch');
      }
      return json(status, { duplicate: false, events: [] });
    },
    '/api/test/echo': (call: Call) => json(200, call.body),
  });
}

describe('actOnce', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('sends a retry of a request without an answer with the first attempt’s id', async () => {
    const server = serve(1);
    await expect(drop()).rejects.toThrow();
    await drop();
    const [first, retry] = server.sent('POST', '/drop');
    expect(idOf(retry)).toBe(idOf(first));
  });

  it('ends the attempt with any answer, even a refusal: the next press is a new command', async () => {
    const server = serve(1, 409);
    await expect(drop()).rejects.toThrow();
    await drop(); // the retry, answered with a refusal
    await drop(); // a new press
    const [first, retry, next] = server.sent('POST', '/drop');
    expect(idOf(retry)).toBe(idOf(first));
    expect(idOf(next)).not.toBe(idOf(retry));
  });

  it('does not reuse an id past the retry window: a drop days later is a new command', async () => {
    const now = vi.spyOn(Date, 'now').mockReturnValue(1_000_000);
    const server = serve(1);
    await expect(drop()).rejects.toThrow();
    now.mockReturnValue(1_000_000 + retryWindowMs + 1);
    await drop();
    const [first, later] = server.sent('POST', '/drop');
    expect(idOf(later)).not.toBe(idOf(first));
  });

  it('keeps apart a request with another body: an admin’s toggle back is its own command', async () => {
    let lost = true;
    const server = fakeServer({
      'POST /api/test/toggle': () => {
        if (lost) {
          lost = false;
          throw new TypeError('Failed to fetch');
        }
        return json(200, {});
      },
    });
    const toggle = (commandId: string, on: boolean) =>
      api.POST('/api/test/toggle' as '/api/seasons/{seasonId}/drop', {
        params: { path: { seasonId } },
        body: { commandId, on } as never,
      });
    await expect(toggle('a', true)).rejects.toThrow();
    await toggle('b', false);
    expect(server.sent('POST', '/toggle').map(idOf)).toEqual(['a', 'b']);
  });

  it('leaves alone a GET, a request outside the API and a body that is not JSON', async () => {
    const onRequest = actOnce.onRequest as (options: {
      request: Request;
      id: string;
    }) => Promise<unknown>;
    const at = (path: string, init?: RequestInit) =>
      new Request(`${globalThis.location.origin}${path}`, init);

    expect(await onRequest({ request: at('/api/pool'), id: '1' })).toBeUndefined();
    expect(
      await onRequest({
        request: at('/styleguide', { method: 'POST', body: '{"commandId":"x"}' }),
        id: '2',
      }),
    ).toBeUndefined();
    expect(
      await onRequest({
        request: at('/api/files', { method: 'POST', body: 'commandId=x' }),
        id: '3',
      }),
    ).toBeUndefined();
  });
});
