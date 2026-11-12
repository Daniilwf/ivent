import { render, screen } from '@testing-library/react';
import type { Schemas } from '../api/client';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';
import { betsFixture, economyFixture, shopFixture } from './inventoryFixtures';

// The inventory on the season screen (D-441): only when the season plays items and I am one of its players; a
// mechanic off is not shown at all, and nothing asks the server for it.

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => {},
}));

const me = '10000000-0000-0000-0000-000000000001';

function season(
  items: boolean,
  mine = true,
  more: { shop?: boolean; bets?: boolean } = {},
): Schemas['SeasonView'] {
  return {
    id: seasonId,
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'finish', type: 'finish' },
    ],
    players: [
      {
        id: me,
        name: 'Вася',
        cellId: 'start',
        points: 0,
        phase: 'idle',
        finishOrder: null,
        avatar: null,
        token: 0,
      },
    ],
    leaderboard: [
      { playerId: me, place: 1, points: 0, cellsToFinish: 1, isFirst: false, provisional: false },
    ],
    me: mine
      ? {
          playerId: me,
          phase: 'idle',
          offer: null,
          choice: null,
          activeRun: null,
          lastCompleted: null,
          nextReroll: null,
          manualEffects: [],
          dropHintMinutes: null,
          dropPenalty: null,
          techRerollOpen: false,
          challengesEnabled: false,
          roll: null,
          unchecked: null,
          finish: null,
        }
      : null,
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    name: 'Тестовый сезон',
    economy: { items, shop: more.shop ?? false, bets: more.bets ?? false },
  };
}

function open(view: Schemas['SeasonView']) {
  const server = fakeServer({
    [`GET /api/seasons/${seasonId}`]: view,
    [`GET /api/seasons/${seasonId}/economy`]: economyFixture({
      shop: shopFixture(),
      bets: betsFixture(),
    }),
  });
  render(<SeasonScreen seasonId={seasonId} onSignedOut={() => {}} />);
  return server;
}

describe('the inventory on the season screen', () => {
  it('is there when the season plays items', async () => {
    open(season(true));

    expect(await screen.findByTestId('item-bird-thief')).toBeInTheDocument();
  });

  it('is not shown, and not asked for, with items off', async () => {
    const server = open(season(false));

    expect(await screen.findByTestId('turn')).toBeInTheDocument();
    expect(screen.queryByTestId('inventory')).not.toBeInTheDocument();
    expect(server.sent('GET', '/economy')).toHaveLength(0);
  });

  it('is not shown to a spectator', async () => {
    const server = open(season(true, false));

    expect(await screen.findByTestId('turn')).toBeInTheDocument();
    expect(screen.queryByTestId('inventory')).not.toBeInTheDocument();
    expect(server.sent('GET', '/economy')).toHaveLength(0);
  });

  it('shows the shop and the bets only when the season plays them (D-447)', async () => {
    open(season(true, true, { shop: true, bets: true }));
    expect(await screen.findByTestId('shop')).toBeInTheDocument();
    expect(screen.getByTestId('bets')).toBeInTheDocument();
  });

  it('keeps the shop and the bets hidden when they are off', async () => {
    open(season(true));
    expect(await screen.findByTestId('item-bird-thief')).toBeInTheDocument();
    expect(screen.queryByTestId('shop')).not.toBeInTheDocument();
    expect(screen.queryByTestId('bets')).not.toBeInTheDocument();
  });

  it('shows the bets without the inventory when bets play without items', async () => {
    const server = open(season(false, true, { bets: true }));
    expect(await screen.findByTestId('bets')).toBeInTheDocument();
    expect(screen.queryByTestId('inventory')).not.toBeInTheDocument();
    expect(screen.queryByTestId('shop')).not.toBeInTheDocument();
    expect(server.sent('GET', '/economy')).toHaveLength(1);
  });
});
