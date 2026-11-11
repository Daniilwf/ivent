import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { ru } from '../i18n/ru';
import { AdminScreen } from './AdminScreen';
import { adminUser, fakeServer, seasonId } from '../test/fakeServer';

// H8: the admin's pages as a whole: open for the admin only (from the menu or by the address), a list of sections
// behind «Разделы» on a phone, the section in the address, the queue count, and no season yet.

vi.mock('../api/realtime', () => ({ watchSeason: () => () => undefined }));

const t = ru.admin;

const player = { ...adminUser, id: 'u1', login: 'vasya', name: 'Вася', role: 'player' as const };

function site(me: typeof adminUser | typeof player) {
  return fakeServer({
    'GET /api/auth/antiforgery': { token: 't', headerName: 'X-CSRF-TOKEN' },
    'GET /api/auth/me': me,
    'GET /api/seasons/current': { id: seasonId },
    'GET /api/seasons/*': {
      id: seasonId,
      name: 'Осень',
      status: 'active',
      deadline: null,
      cells: [],
      players: [],
      leaderboard: [],
      me: null,
      lastSequence: 1,
    },
    'GET /api/admin/seasons/*/proofs': [],
    'GET /api/admin/seasons/*/players': [],
  });
}

describe('The admin pages', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    globalThis.history.pushState(null, '', '/');
  });

  it('open for the admin by the address, the proof queue first', async () => {
    globalThis.history.pushState(null, '', '/admin');
    site(adminUser);

    render(<App />);

    expect(await screen.findByTestId('admin')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(t.sections.proofs);
  });

  it('do not open for a player: the address shows the game', async () => {
    globalThis.history.pushState(null, '', '/admin/players');
    const server = site(player);

    render(<App />);

    await waitFor(() => {
      expect(server.sent('GET', `/api/seasons/${seasonId}`).length).toBeGreaterThan(0);
    });
    expect(screen.queryByTestId('admin')).toBeNull();
    expect(server.calls.some((c) => c.path.startsWith('/api/admin'))).toBe(false);
    // The game's section is the current one in the header, and the menu offers no admin's pages (D-202)
    const nav = screen.getByRole('navigation', { name: ru.nav.label });
    expect(within(nav).getByRole('link', { name: ru.nav.season })).toHaveAttribute(
      'aria-current',
      'page',
    );
    await userEvent.click(screen.getByTestId('user-menu'));
    expect(await screen.findByTestId('my-profile')).toBeInTheDocument();
    expect(screen.queryByTestId('to-admin')).toBeNull();
  });

  it('open from the admin’s menu and lead back to the game', async () => {
    site(adminUser);
    render(<App />);

    await userEvent.click(await screen.findByTestId('user-menu'));
    await userEvent.click(await screen.findByTestId('to-admin'));
    expect(await screen.findByTestId('admin')).toBeInTheDocument();
    expect(globalThis.location.pathname).toBe('/admin');
    // Opened from the menu, the page gives its heading the focus (D-202)
    await waitFor(() => {
      expect(screen.getByRole('heading', { level: 1 })).toHaveFocus();
    });

    // Back to the game by the header's sections, as from any page of the site (D-202)
    await userEvent.click(
      within(screen.getByRole('navigation', { name: ru.nav.label })).getByRole('link', {
        name: ru.nav.season,
      }),
    );
    expect(globalThis.location.pathname).toBe('/');
    await waitFor(() => {
      expect(screen.queryByTestId('admin')).toBeNull();
    });
  });

  it('offer no admin item to a player', async () => {
    site(player);
    render(<App />);

    await userEvent.click(await screen.findByTestId('user-menu'));
    expect(await screen.findByTestId('change-password')).toBeInTheDocument();
    expect(screen.queryByTestId('to-admin')).toBeNull();
  });

  it('switch sections from the phone’s sheet and put the section in the address', async () => {
    site(adminUser);
    globalThis.history.pushState(null, '', '/admin');
    render(<App />);

    await userEvent.click(await screen.findByTestId('admin-sections'));
    const sheet = await screen.findByRole('dialog');
    const links = within(sheet).getAllByRole('link');
    expect(links.map((l) => l.textContent)).toEqual(
      expect.arrayContaining([t.sections.proofs, t.sections.players, t.sections.rules]),
    );
    expect(within(sheet).getByTestId('admin-nav-proofs')).toHaveAttribute('aria-current', 'page');
    await userEvent.click(within(sheet).getByTestId('admin-nav-players'));

    expect(globalThis.location.pathname).toBe('/admin/players');
    await waitFor(() => {
      expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(t.sections.players);
    });
    expect(screen.queryByRole('dialog')).toBeNull();
    // The focus comes on the next frame (the router's usePageHeading)
    await waitFor(() => {
      expect(screen.getByRole('heading', { level: 1 })).toHaveFocus();
    });
  });

  it('show the count of runs waiting for a check on the sections button', async () => {
    fakeServer({
      'GET /api/admin/seasons/*/proofs': [
        {
          runId: 'r1',
          playerId: 'p1',
          playerName: 'Вася',
          gameTitle: 'Celeste',
          completedAt: null,
          reachedFinish: false,
          status: null,
          links: [],
          note: null,
          witnessName: null,
          difficulty: null,
          hours: null,
          diceTotal: 4,
          decidesFinish: false,
          files: [],
          rollClosed: false,
        },
      ],
    });
    render(<AdminScreen section="log" currentSeasonId={seasonId} user={adminUser} />);

    expect(await screen.findByTestId('admin-sections')).toHaveTextContent('1');
  });

  it('without a season send the season pages to «Сезон»', async () => {
    fakeServer({ 'GET /api/seasons': [] });
    render(<AdminScreen section="players" currentSeasonId={null} user={adminUser} />);

    expect(await screen.findByText(t.noSeasonTitle)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: t.toSeason }));
    expect(globalThis.location.pathname).toBe('/admin/season');
  });

  it('open the section named in the address', () => {
    fakeServer({ 'GET /api/admin/seasons/*/proofs': [] });
    const { unmount } = render(
      <AdminScreen section="errors" currentSeasonId={seasonId} user={adminUser} />,
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(t.sections.errors);
    unmount();
  });

  it('say «no such page» at an address of a section they do not have', async () => {
    globalThis.history.pushState(null, '', '/admin/nonsense');
    const server = site(adminUser);
    render(<App />);

    expect(await screen.findByTestId('page-not-found')).toBeInTheDocument();
    expect(screen.queryByTestId('admin')).toBeNull();
    expect(server.calls.some((c) => c.path.startsWith('/api/admin'))).toBe(false);
  });

  // K1A: the season the admin opened stays after a reload and in a new tab, and every season page names it
  const other = '5ea50000-0000-0000-0000-000000000002';
  const seasons = [
    {
      id: seasonId,
      name: 'Сезон разработки',
      status: 'active',
      deadline: null,
      createdAt: '2026-10-02T00:00:00Z',
    },
    {
      id: other,
      name: 'Демо-сезон',
      status: 'active',
      deadline: null,
      createdAt: '2026-10-01T00:00:00Z',
    },
  ];
  const seasonView = (id: string, name: string) => ({
    id,
    name,
    status: 'active',
    deadline: null,
    cells: [],
    players: [],
    leaderboard: [],
    me: null,
    lastSequence: 1,
  });

  it('keep the season the admin opened after a reload and name it on the season pages', async () => {
    const server = fakeServer({
      'GET /api/seasons': seasons,
      [`GET /api/seasons/${seasonId}`]: seasonView(seasonId, 'Сезон разработки'),
      [`GET /api/seasons/${other}`]: seasonView(other, 'Демо-сезон'),
      'GET /api/admin/seasons/*/proofs': [],
    });
    const first = render(
      <AdminScreen section="season" currentSeasonId={seasonId} user={adminUser} />,
    );
    await userEvent.click(await screen.findByTestId(`season-open-${other}`));
    first.unmount();
    const before = server.calls.length;

    // A reload: the page starts again with the newest season as the current one
    render(<AdminScreen section="proofs" currentSeasonId={seasonId} user={adminUser} />);

    expect(await screen.findByTestId('admin-season-name')).toHaveTextContent('Демо-сезон');
    await waitFor(() => {
      expect(server.calls.some((c) => c.path === `/api/admin/seasons/${other}/proofs`)).toBe(true);
    });
    expect(
      server.calls.slice(before).some((c) => c.path === `/api/admin/seasons/${seasonId}/proofs`),
    ).toBe(false);
  });

  it('open a season just created, though the list read before did not have it', async () => {
    // K1A: the kept choice must not fall back to the current season because the list was read before the new one
    const created: { id: string; name: string }[] = [];
    fakeServer({
      'GET /api/seasons': () => ({
        body: [
          ...created.map((s) => ({
            ...s,
            status: 'preparing',
            deadline: null,
            createdAt: '2026-10-03T00:00:00Z',
          })),
          ...seasons,
        ],
      }),
      'POST /api/admin/seasons': (call) => {
        const body = call.body as { seasonId: string; name: string };
        created.push({ id: body.seasonId, name: body.name });
        return { body: { events: [] } };
      },
      'GET /api/seasons/*': (call) => {
        const id = call.path.split('/').at(-1) ?? '';
        const found = created.find((s) => s.id === id);
        return {
          body: found
            ? { ...seasonView(id, found.name), status: 'preparing' }
            : seasonView(id, id === other ? 'Демо-сезон' : 'Сезон разработки'),
        };
      },
      'GET /api/admin/seasons/*/proofs': [],
    });
    render(<AdminScreen section="season" currentSeasonId={seasonId} user={adminUser} />);

    await userEvent.type(await screen.findByTestId('season-name'), 'Зима');
    await userEvent.click(screen.getByTestId('season-create'));

    await waitFor(() => {
      expect(screen.getByTestId('season-status')).toHaveTextContent('Зима');
    });
    expect(globalThis.localStorage.getItem('ge.admin.season')).toBe(created[0]?.id);
  });

  it('fall back to the current season when the one kept is gone', async () => {
    fakeServer({
      'GET /api/seasons': seasons.slice(0, 1),
      'GET /api/admin/seasons/*/proofs': [],
    });
    globalThis.localStorage.setItem('ge.admin.season', '5ea50000-0000-0000-0000-00000000dead');

    render(<AdminScreen section="proofs" currentSeasonId={seasonId} user={adminUser} />);

    expect(await screen.findByTestId('admin-season-name')).toHaveTextContent('Сезон разработки');
  });
});
