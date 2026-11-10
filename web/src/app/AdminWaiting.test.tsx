import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { forgetWaitingProofs } from '../admin/waitingProofs';
import { ru } from '../i18n/ru';
import { adminUser, fakeServer, seasonId } from '../test/fakeServer';

// D-359, D-263: the admin's count of runs waiting for a proof check, on the way to «Админка»: the menu button and its
// item; the admin only, live by the season's hub, hidden at zero, 99+ at most on the screen, the number in the label.

type Update = { types: string[] };
let hub: ((updates: Update[]) => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: Update[]) => void) => {
    hub = onChange;
    // The hub's first answer is the join: skipped (D-202)
    onChange([]);
    return () => {
      hub = null;
    };
  },
}));

const player = { ...adminUser, id: 'u1', login: 'vasya', name: 'Вася', role: 'player' as const };
const item = (n: number) => ({
  runId: `r${String(n)}`,
  playerId: 'p1',
  playerName: 'Сова',
  gameTitle: `Игра ${String(n)}`,
  completedAt: '2026-09-20T09:00:00+00:00',
  reachedFinish: false,
  status: 'pending',
  links: [],
  note: null,
  witnessName: null,
  difficulty: 'normal',
  hours: 10,
  diceTotal: 5,
  decidesFinish: false,
  files: [],
  rollClosed: false,
});

function site(me: typeof adminUser | typeof player, waiting: () => number) {
  return fakeServer({
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
    'GET /api/seasons/*/feed': { entries: [], nextBefore: null, players: [], games: [], runs: [] },
    'GET /api/admin/seasons/*/proofs': () => ({
      body: Array.from({ length: waiting() }, (_, i) => item(i)),
    }),
    'GET /api/admin/seasons/*/players': [],
  });
}

describe('the admin’s count of proofs to check', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    forgetWaitingProofs();
    globalThis.history.pushState(null, '', '/');
  });

  it('stands on the menu button and on «Админка» with the number in the label', async () => {
    site(adminUser, () => 3);
    render(<App />);

    const button = await screen.findByRole('button', {
      name: ru.shell.menuWaiting(adminUser.name, 3),
    });
    expect(within(button).getByTestId('admin-waiting')).toHaveTextContent('3');
    await userEvent.click(button);
    const toAdmin = await screen.findByTestId('to-admin');
    expect(toAdmin).toHaveAccessibleName(ru.nav.adminWaiting(3));
    expect(within(toAdmin).getByText('3')).toBeInTheDocument();
  });

  it('is hidden at zero', async () => {
    const server = site(adminUser, () => 0);
    render(<App />);

    await waitFor(() => {
      expect(server.sent('GET', '/proofs')).toHaveLength(1);
    });
    const button = screen.getByTestId('user-menu');
    expect(button).toHaveAccessibleName(ru.shell.menu(adminUser.name));
    expect(screen.queryByTestId('admin-waiting')).toBeNull();
    await userEvent.click(button);
    expect(await screen.findByTestId('to-admin')).toHaveAccessibleName(ru.nav.admin);
  });

  it('shows 99+ above 99, the label keeps the number', async () => {
    site(adminUser, () => 120);
    render(<App />);

    const button = await screen.findByRole('button', {
      name: ru.shell.menuWaiting(adminUser.name, 120),
    });
    expect(within(button).getByTestId('admin-waiting')).toHaveTextContent('99+');
  });

  it('is never asked for nor shown to a player', async () => {
    const server = site(player, () => 3);
    render(<App />);

    await waitFor(() => {
      expect(server.sent('GET', `/api/seasons/${seasonId}`).length).toBeGreaterThan(0);
    });
    expect(server.calls.some((c) => c.path.startsWith('/api/admin'))).toBe(false);
    expect(screen.queryByTestId('admin-waiting')).toBeNull();
  });

  it('follows the season’s hub: a completion adds, a check removes; other updates ask nothing', async () => {
    let waiting = 1;
    const server = site(adminUser, () => waiting);
    render(<App />);
    expect(await screen.findByTestId('admin-waiting')).toHaveTextContent('1');

    // A roll does not change the queue: no read
    act(() => {
      hub?.([{ types: ['game-rolled'] }]);
    });
    waiting = 2;
    act(() => {
      hub?.([{ types: ['run-completed'] }]);
    });
    await waitFor(() => {
      expect(screen.getByTestId('admin-waiting')).toHaveTextContent('2');
    });
    expect(server.sent('GET', '/proofs')).toHaveLength(2);
  });

  it('takes the admin page’s own queue there instead of asking again', async () => {
    globalThis.history.pushState(null, '', '/admin');
    const server = site(adminUser, () => 2);
    render(<App />);

    expect(await screen.findByTestId('admin')).toBeInTheDocument();
    expect(await screen.findByTestId('admin-waiting')).toHaveTextContent('2');
    // One read: the admin page's
    expect(server.sent('GET', '/proofs')).toHaveLength(1);
  });
});
