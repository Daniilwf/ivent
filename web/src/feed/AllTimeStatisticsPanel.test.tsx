import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import type { SeasonUpdate } from '../api/realtime';
import { ru } from '../i18n/ru';
import { fakeServer, json, seasonId } from '../test/fakeServer';
import { ProfileScreen } from './ProfileScreen';

const { watchSeason } = vi.hoisted(() => ({
  watchSeason: vi.fn<(_id: string, _update: (updates: SeasonUpdate[]) => void) => () => void>(),
}));

vi.mock('../api/realtime', () => ({ watchSeason }));

const userId = 'aaaaaaaa-0000-0000-0000-000000000001';
const path = '/api/me/statistics';
const profile: Schemas['ProfileView'] = {
  id: userId,
  name: 'Лиса',
  avatar: null,
  achievements: [],
  completed: 2,
  reviews: [],
  seasons: [
    {
      seasonId,
      seasonName: 'Сейчас',
      status: 'active',
      playerId: 'player',
      points: 1,
      place: null,
      token: 0,
    },
  ],
};
const totals = {
  seasonCount: 2,
  completedRuns: 2,
  droppedRuns: 1,
  totalHours: 9.5,
  bestRating: 9,
  worstRating: 6,
  attacksMade: 1,
  attacksReceived: 2,
  bestRoll: { runId: 'current', sum: 8, minimum: 2, maximum: 8, luck: 1 },
  worstRoll: { runId: 'archived', sum: 1, minimum: 1, maximum: 4, luck: 0 },
};
const routes = {
  [`/api/users/${userId}`]: profile,
  '/api/auth/sessions': [],
};

describe('all-time profile statistics', () => {
  beforeEach(() => {
    watchSeason.mockImplementation((_id, update) => {
      update([]);
      return vi.fn();
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    watchSeason.mockReset();
    globalThis.history.replaceState(null, '', '/');
  });

  it('does not request a private recap when reading another player’s profile', async () => {
    const server = fakeServer({ ...routes, [path]: totals });
    render(<ProfileScreen userId={userId} meId="someone-else" onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: profile.name })).toBeVisible();
    expect(screen.getByTestId('profile-completed')).toHaveTextContent('Пройдены 2 игры');
    expect(screen.queryByRole('region', { name: 'Сводка за всё время' })).not.toBeInTheDocument();
    expect(server.sent('GET', path)).toHaveLength(0);
  });

  it('shows private totals, preserves zero luck and refreshes the affected season after a reject', async () => {
    let stats = totals;
    let notify: (updates: SeasonUpdate[]) => void = vi.fn();
    watchSeason.mockImplementationOnce((_id, update) => {
      notify = update;
      update([]);
      return vi.fn();
    });
    const server = fakeServer({ ...routes, [path]: () => json(200, stats) });
    render(<ProfileScreen userId={userId} meId={userId} onSignedOut={vi.fn()} />);

    const panel = within(await screen.findByRole('region', { name: 'Сводка за всё время' }));
    expect(await panel.findByText('9,5 ч')).toBeVisible();
    expect(screen.queryByTestId('profile-completed')).not.toBeInTheDocument();
    expect(panel.getByText('0 %')).toBeVisible();
    const range = panel.getByText('1–4');
    expect(range).toHaveClass('whitespace-nowrap');
    expect(range.parentElement).toHaveTextContent('Сумма 1, диапазон 1–4');
    expect(panel.getByText('9 из 10')).toBeVisible();
    expect(watchSeason).toHaveBeenCalledWith(seasonId, expect.any(Function), undefined);
    expect(server.sent('GET', path)[0]?.query.size).toBe(0);

    stats = { ...totals, completedRuns: 1, totalHours: 3, bestRoll: totals.worstRoll };
    act(() => {
      notify([{ seasonId, fromSequence: 14, toSequence: 15, types: ['proof-rejected'] }]);
    });

    expect(await panel.findByText('3 ч')).toBeVisible();
    expect(panel.queryByText('9,5 ч')).not.toBeInTheDocument();
    expect(server.sent('GET', path)).toHaveLength(2);
  });

  it('allows retry after a failed read and shows an empty state for an owner without seasons', async () => {
    let attempts = 0;
    fakeServer({
      ...routes,
      [`/api/users/${userId}`]: { ...profile, seasons: [] },
      [path]: () => {
        attempts++;
        return attempts === 1 ? json(503) : json(200, { ...totals, seasonCount: 0 });
      },
    });
    render(<ProfileScreen userId={userId} meId={userId} onSignedOut={vi.fn()} />);

    const panel = within(await screen.findByRole('region', { name: 'Сводка за всё время' }));
    expect(await panel.findByRole('alert')).toBeVisible();
    await userEvent.click(panel.getByRole('button', { name: ru.ui.retry }));
    expect(await panel.findByText('Сводка появится после участия в сезоне.')).toBeVisible();
    expect(watchSeason).not.toHaveBeenCalled();
  });

  it('keeps the public completion count visible when an older server lacks the recap route', async () => {
    fakeServer(routes);
    render(<ProfileScreen userId={userId} meId={userId} onSignedOut={vi.fn()} />);

    const panel = within(await screen.findByRole('region', { name: 'Сводка за всё время' }));
    expect(await panel.findByText('Пройдены 2 игры')).toBeVisible();
    expect(panel.getByText('Сводка пока недоступна.')).toBeVisible();
  });
});
