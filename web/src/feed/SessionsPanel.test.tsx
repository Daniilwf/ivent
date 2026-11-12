import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { answer, fakeServer, json } from '../test/fakeServer';
import { deviceOf } from './devices';
import { SessionsPanel } from './SessionsPanel';

// «Где я вошёл» (D-464 (1), D-466): my live sessions with this device first, «Выйти» on another one, «Выйти на
// остальных устройствах»; every state.

const t = ru.sessions;

const here: Schemas['SessionView'] = {
  id: '11111111-0000-0000-0000-000000000001',
  userAgent:
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile Safari/604.1',
  createdAt: '2026-11-10T09:00:00Z',
  lastSeenAt: '2026-11-11T09:00:00Z',
  current: true,
};
const laptop: Schemas['SessionView'] = {
  id: '11111111-0000-0000-0000-000000000002',
  userAgent:
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36',
  createdAt: '2026-11-01T09:00:00Z',
  lastSeenAt: '2026-11-09T18:30:00Z',
  current: false,
};

describe('SessionsPanel', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists this device first, marked, and the others with their last visit', async () => {
    fakeServer({ 'GET /api/auth/sessions': [here, laptop] });
    render(<SessionsPanel onSignedOut={vi.fn()} />);

    const current = await screen.findByTestId('session-current');
    expect(current).toHaveTextContent('Safari, iPhone');
    expect(current).toHaveTextContent(t.thisDevice);
    expect(within(current).queryByTestId('session-end')).toBeNull();
    const other = screen.getByTestId('session-other');
    expect(other).toHaveTextContent('Chrome, Windows');
    expect(other).toHaveTextContent('Последний заход: 9 ноября');
    expect(
      within(other).getByRole('button', { name: t.endLabel('Chrome, Windows') }),
    ).toBeVisible();
  });

  it('signs out another device and loads the list again', async () => {
    let list = [here, laptop];
    const server = fakeServer({
      'GET /api/auth/sessions': () => ({ body: list }),
      'POST /api/auth/sessions/*/end': () => {
        list = [here];
        return { status: 204 };
      },
    });
    render(<SessionsPanel onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('session-end'));

    expect(await screen.findByText(t.ended)).toBeInTheDocument();
    const sent = server.sent('POST', `/${laptop.id}/end`)[0]?.body as
      { commandId?: unknown } | undefined;
    expect(typeof sent?.commandId).toBe('string');
    await waitFor(() => {
      expect(screen.queryByTestId('session-other')).toBeNull();
    });
    expect(screen.getByTestId('sessions-only-this')).toHaveTextContent(t.onlyThis);
  });

  it('signs out every other device at once', async () => {
    const server = fakeServer({
      'GET /api/auth/sessions': [here, laptop, { ...laptop, id: 'x3' }],
      'POST /api/auth/sessions/end-others': { ended: 2 },
    });
    render(<SessionsPanel onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('sessions-end-others'));

    expect(await screen.findByText(t.endedOthers(2))).toBeInTheDocument();
    expect(server.sent('POST', '/end-others')).toHaveLength(1);
  });

  it('works from the keyboard', async () => {
    const server = fakeServer({
      'GET /api/auth/sessions': [here, laptop],
      'POST /api/auth/sessions/end-others': { ended: 1 },
    });
    render(<SessionsPanel onSignedOut={vi.fn()} />);
    await screen.findByTestId('session-other');

    await userEvent.tab();
    expect(screen.getByTestId('session-end')).toHaveFocus();
    await userEvent.tab();
    expect(screen.getByTestId('sessions-end-others')).toHaveFocus();
    await userEvent.keyboard('{Enter}');

    await waitFor(() => {
      expect(server.sent('POST', '/end-others')).toHaveLength(1);
    });
  });

  it('says only this device when there are no others', async () => {
    fakeServer({ 'GET /api/auth/sessions': [here] });
    render(<SessionsPanel onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('sessions-only-this')).toBeInTheDocument();
    expect(screen.queryByTestId('sessions-end-others')).toBeNull();
  });

  it('shows the skeleton while loading', () => {
    fakeServer({ 'GET /api/auth/sessions': () => new Promise<never>(() => {}) });
    render(<SessionsPanel onSignedOut={vi.fn()} />);

    expect(screen.getByTestId('sessions-loading')).toHaveAttribute('aria-busy', 'true');
  });

  it('says a failed load and loads again on request', async () => {
    let fail = true;
    fakeServer({ 'GET /api/auth/sessions': () => (fail ? json(500) : json(200, [here])) });
    render(<SessionsPanel onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: t.errorTitle })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));
    expect(await screen.findByTestId('session-current')).toBeInTheDocument();
  });

  it('says a failed sign-out of another device', async () => {
    fakeServer({
      'GET /api/auth/sessions': [here, laptop],
      'POST /api/auth/sessions/end-others': answer(500),
    });
    render(<SessionsPanel onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('sessions-end-others'));

    expect(await screen.findByRole('alert')).toHaveTextContent(t.failed);
  });

  it('signs out the page when the session ended meanwhile', async () => {
    fakeServer({ 'GET /api/auth/sessions': () => json(401) });
    const onSignedOut = vi.fn();
    render(<SessionsPanel onSignedOut={onSignedOut} />);

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalled();
    });
  });
});

describe('deviceOf', () => {
  it.each([
    [here.userAgent, 'Safari, iPhone'],
    [laptop.userAgent, 'Chrome, Windows'],
    [
      'Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/130.0 Safari/537.36 Edg/130.0',
      'Edge, Windows',
    ],
    [
      'Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/130.0 YaBrowser/24.10 Mobile Safari/537.36',
      `${t.browsers.yandex}, Android`,
    ],
    [
      'Mozilla/5.0 (Macintosh; Intel Mac OS X 14.5; rv:132.0) Gecko/20100101 Firefox/132.0',
      'Firefox, macOS',
    ],
    [
      'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 OPR/114.0 Chrome/130.0 Safari/537.36',
      'Opera, Linux',
    ],
    ['curl/8.0', t.unknownDevice],
    [null, t.unknownDevice],
    [undefined, t.unknownDevice],
  ])('names %s', (ua, name) => {
    expect(deviceOf(ua)).toBe(name);
  });
});
