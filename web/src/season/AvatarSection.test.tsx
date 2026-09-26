import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { AvatarSection } from './AvatarSection';

// D-117: the player's avatar — a picture from the device or a GIF by link, set on the account; removed with none.

const stored = {
  id: '50000000-0000-0000-0000-000000000001',
  mediaType: 'image/gif',
  width: 20,
  height: 20,
  frames: 1,
  url: '/api/files/5',
  thumbnailUrl: '/api/files/5/thumbnail',
  duplicate: false,
};

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

type Sent = { url: string; method: string; body: unknown };

type Link = { id: string; url: string; thumbnailUrl: string };

// The account (GET /api/auth/me) answers with `avatar`; every other request goes to `answer` and is recorded
function serve(answer: (url: string, method: string) => Response, avatar: Link | null = null) {
  const sent: Sent[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: Request | string, init?: RequestInit) => {
      const request = typeof input === 'string' ? null : input;
      const url = request?.url ?? (input as string);
      const method = request?.method ?? init?.method ?? 'GET';
      if (method === 'GET' && url.endsWith('/api/auth/me')) {
        return json(200, {
          id: 'u',
          login: 'vasya',
          name: 'Вася',
          role: 'player',
          mustChangePassword: false,
          avatar,
        });
      }
      const body: unknown = request
        ? await request
            .clone()
            .json()
            .catch(() => null)
        : init?.body;
      sent.push({ url, method, body });
      return answer(url, method);
    }),
  );
  return sent;
}

const picture = () => new File([new Uint8Array([71, 73, 70])], 'me.gif', { type: 'image/gif' });

describe('My avatar', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('uploads a picture and sets it as the avatar', async () => {
    const sent = serve((url) =>
      url.endsWith('/api/files')
        ? json(200, stored)
        : json(200, { duplicate: false, avatar: null }),
    );
    const onChanged = vi.fn();
    render(<AvatarSection onChanged={onChanged} />);

    await userEvent.upload(screen.getByTestId('avatar-file'), picture());

    await vi.waitFor(() => {
      expect(onChanged).toHaveBeenCalled();
    });
    const put = sent.find((s) => s.method === 'PUT');
    expect(put?.url).toMatch(/\/api\/auth\/me\/avatar$/);
    expect(put?.body).toMatchObject({ fileId: stored.id });
  });

  it('takes a GIF by link through the server and sets it', async () => {
    const sent = serve((url) =>
      url.endsWith('/api/files/from-url')
        ? json(200, stored)
        : json(200, { duplicate: false, avatar: null }),
    );
    const onChanged = vi.fn();
    render(<AvatarSection onChanged={onChanged} />);

    await userEvent.type(screen.getByTestId('avatar-link'), 'https://media.tenor.com/cat.gif');
    await userEvent.click(screen.getByRole('button', { name: ru.avatar.useLink }));

    await vi.waitFor(() => {
      expect(onChanged).toHaveBeenCalled();
    });
    expect(sent.find((s) => s.url.endsWith('/from-url'))?.body).toMatchObject({
      url: 'https://media.tenor.com/cat.gif',
    });
    expect(sent.find((s) => s.method === 'PUT')?.body).toMatchObject({ fileId: stored.id });
  });

  it('says in Russian that a link from another site is not taken', async () => {
    serve(() => json(422, { title: 'refused', status: 422, code: 'file.hostNotAllowed' }));
    const onChanged = vi.fn();
    render(<AvatarSection onChanged={onChanged} />);

    await userEvent.type(screen.getByTestId('avatar-link'), 'https://example.com/cat.gif');
    await userEvent.click(screen.getByRole('button', { name: ru.avatar.useLink }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.upload.errors['file.hostNotAllowed'],
    );
    expect(onChanged).not.toHaveBeenCalled();
  });

  it('asks for a link before sending an empty one', async () => {
    const sent = serve(() => json(500, {}));
    render(<AvatarSection onChanged={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: ru.avatar.useLink }));

    expect(screen.getByRole('alert')).toHaveTextContent(ru.avatar.linkRequired);
    expect(sent.filter((x) => x.method !== 'GET')).toHaveLength(0);
  });

  it('shows the current avatar of the account and removes it', async () => {
    const sent = serve(() => json(200, { duplicate: false, avatar: null }), {
      id: stored.id,
      url: stored.url,
      thumbnailUrl: stored.thumbnailUrl,
    });
    const onChanged = vi.fn();
    render(<AvatarSection onChanged={onChanged} />);

    expect(await screen.findByRole('img', { name: ru.avatar.current })).toHaveAttribute(
      'src',
      stored.thumbnailUrl,
    );
    await userEvent.click(screen.getByRole('button', { name: ru.avatar.remove }));

    await vi.waitFor(() => {
      expect(onChanged).toHaveBeenCalled();
    });
    expect(sent.find((x) => x.method === 'PUT')?.body).toMatchObject({ fileId: null });
  });

  it('names the hourly limit of downloads when the server answers 429 without a code', async () => {
    serve(() => new Response('', { status: 429 }));
    render(<AvatarSection onChanged={vi.fn()} />);

    await userEvent.type(screen.getByTestId('avatar-link'), 'https://media.tenor.com/cat.gif');
    await userEvent.click(screen.getByRole('button', { name: ru.avatar.useLink }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.upload.errors['file.downloadsPerHour'],
    );
  });

  it('says in Russian when the server refuses the avatar', async () => {
    serve((url) =>
      url.endsWith('/api/files')
        ? json(200, stored)
        : json(409, { title: 'refused', status: 409, code: 'account.avatarNotYours' }),
    );
    render(<AvatarSection onChanged={vi.fn()} />);

    await userEvent.upload(screen.getByTestId('avatar-file'), picture());

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.rejection['account.avatarNotYours'],
    );
  });
});
