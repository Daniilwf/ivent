import type { Schemas } from '../api/client';
import { seasonBoard } from '../board/seasonBoard';
import { seasonPicture } from './seasonView';

const vasya = '10000000-0000-0000-0000-000000000001';

test('the map and the leaderboard use the small animated avatar (D-945)', () => {
  const avatar = { id: 'f', url: '/api/files/f', thumbnailUrl: '/api/files/f/thumbnail' };
  const season: Schemas['SeasonView'] = {
    id: '5ea50000-0000-0000-0000-000000000001',
    name: 'Тестовый сезон',
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'finish', type: 'finish' },
    ],
    edges: [],
    zones: [],
    mapMode: 'linear',
    players: [
      {
        id: vasya,
        name: 'Вася',
        cellId: 'start',
        points: 3,
        phase: 'idle',
        finishOrder: null,
        avatar,
        token: 0,
      },
    ],
    leaderboard: [
      {
        playerId: vasya,
        place: 1,
        points: 3,
        cellsToFinish: 1,
        isFirst: false,
        provisional: false,
      },
    ],
    me: null,
    lastSequence: 0,
  };

  const picture = seasonPicture(season, seasonBoard(season));

  expect(picture.players[0]?.avatar).toBe(avatar.thumbnailUrl);
  expect(picture.rows[0]?.player.avatar).toBe(avatar.thumbnailUrl);
});
