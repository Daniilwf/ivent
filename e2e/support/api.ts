import { expect, request, type APIRequestContext, type APIResponse } from '@playwright/test';
import type { components } from '../../web/src/api/schema.ts';

// The site's API as a signed-in user, for the scenarios' setup and for the checks a screen cannot make (a refusal of a
// forged request). The types come from the generated OpenAPI client of the frontend: never written by hand.

export type Schemas = components['schemas'];

/** The development seed's admin (src/GameEvent.Web/Hosting/DevSeed.cs, the dev-only DevSeed:Password) */
export const seedAdmin = { login: 'admin', password: 'dev-password' };

/** A new command id: every write carries one (D-68) */
export const commandId = () => crypto.randomUUID();

export class Api {
  private constructor(
    readonly context: APIRequestContext,
    private csrf: { headerName: string; token: string },
  ) {}

  /** Signs in through the API: the antiforgery token, the sign-in, then the token bound to the user */
  static async signIn(baseURL: string, login: string, password: string): Promise<Api> {
    const context = await request.newContext({ baseURL });
    const api = new Api(context, await antiforgery(context));
    const answer = await api.send('POST', '/api/auth/login', { login, password });
    expect(answer.status(), `sign-in of ${login}: ${await answer.text()}`).toBe(200);
    api.csrf = await antiforgery(context);
    return api;
  }

  /** A write or a read, as it is: for the refusals */
  send(method: 'GET' | 'POST' | 'PUT', path: string, data?: object): Promise<APIResponse> {
    return this.context.fetch(path, {
      method,
      ...(data === undefined ? {} : { data }),
      headers: method === 'GET' ? {} : { [this.csrf.headerName]: this.csrf.token },
    });
  }

  async get<T>(path: string): Promise<T> {
    return ok<T>(await this.send('GET', path), `GET ${path}`);
  }

  /** A write that must be accepted; the command id is added unless the body has one */
  async post<T = Schemas['CommandResponse']>(path: string, data: object = {}): Promise<T> {
    return ok<T>(
      await this.send('POST', path, { commandId: commandId(), ...data }),
      `POST ${path}`,
    );
  }

  async put<T = Schemas['CommandResponse']>(path: string, data: object): Promise<T> {
    return ok<T>(await this.send('PUT', path, { commandId: commandId(), ...data }), `PUT ${path}`);
  }

  /** The session's cookies, to open the site in a browser already signed in */
  async cookies() {
    return (await this.context.storageState()).cookies;
  }

  dispose() {
    return this.context.dispose();
  }
}

async function antiforgery(context: APIRequestContext) {
  const answer = await context.get('/api/auth/antiforgery');
  expect(answer.ok()).toBe(true);
  return (await answer.json()) as { headerName: string; token: string };
}

async function ok<T>(answer: APIResponse, what: string): Promise<T> {
  const text = await answer.text();
  expect(answer.ok(), `${what} → ${String(answer.status())} ${text}`).toBe(true);
  return (text === '' ? undefined : JSON.parse(text)) as T;
}
