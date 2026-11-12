import { json } from '../test/fakeServer';
import { uploadFile, UploadError } from './files';

// D-617: a file over the site's limit is refused before it is sent, by the limits the site says it is configured with
// (`/api/status`), not by numbers of the page's own (review of K1A-10)

const stored = {
  id: '50000000-0000-0000-0000-000000000001',
  mediaType: 'image/png',
  width: 20,
  height: 20,
  frames: 1,
  url: '/api/files/5',
  thumbnailUrl: '/api/files/5/thumbnail',
  duplicate: false,
};

const limits = { picture: 1000, gif: 500, bugScreenshot: 300, mapBackground: 800 };

type Sent = { url: string; method: string };

/** The site: `/api/status` answers `status` (or fails), an upload is stored; every request is recorded */
function site(status: object | null) {
  const sent: Sent[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((input: Request | string, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.url;
      const method = typeof input === 'string' ? (init?.method ?? 'GET') : input.method;
      sent.push({ url, method });
      const answer = url.endsWith('/api/status')
        ? status === null
          ? json(500, {})
          : json(200, status)
        : json(200, stored);
      return Promise.resolve(answer);
    }),
  );
  return {
    posts: () => sent.filter((x) => x.method === 'POST').map((x) => new URL(x.url).pathname),
  };
}

const status = {
  maintenance: false,
  version: '1',
  environment: 'test',
  testTools: false,
  uploads: limits,
};

const file = (size: number, type = 'image/png') => {
  const made = new File([new Uint8Array([1])], 'a', { type });
  Object.defineProperty(made, 'size', { value: size });
  return made;
};

const refusal = (upload: Promise<unknown>) =>
  upload.then(
    () => null,
    (e: unknown) => (e instanceof UploadError ? e.code : 'other'),
  );

describe('uploadFile', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('refuses a picture over the limit the site names and sends one within it', async () => {
    const server = site(status);

    expect(await refusal(uploadFile(file(1001)))).toBe('file.tooLarge');
    expect(server.posts()).toHaveLength(0);

    expect(await refusal(uploadFile(file(1000)))).toBeNull();
    expect(server.posts()).toEqual(['/api/files']);
  });

  it('holds a GIF, a bug screenshot and a map background to their own limits', async () => {
    const server = site(status);

    expect(await refusal(uploadFile(file(501, 'image/gif')))).toBe('file.tooLarge');
    expect(await refusal(uploadFile(file(301), 'a.png', '/api/bug-reports/screenshot'))).toBe(
      'file.tooLarge',
    );
    expect(
      await refusal(uploadFile(file(801), 'a.png', '/api/admin/seasons/s/map/background')),
    ).toBe('file.tooLarge');
    expect(server.posts()).toHaveLength(0);

    expect(
      await refusal(uploadFile(file(800), 'a.png', '/api/admin/seasons/s/map/background')),
    ).toBeNull();
    expect(server.posts()).toEqual(['/api/admin/seasons/s/map/background']);
  });

  it('sends the file when the site did not say its limits, and the server answers for itself', async () => {
    const server = site(null);

    expect(await refusal(uploadFile(file(40 * 1024 * 1024)))).toBeNull();
    expect(server.posts()).toEqual(['/api/files']);
  });
});
