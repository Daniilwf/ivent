import assert from 'node:assert/strict';
import test from 'node:test';
import { assertStage6MemoryReads } from './memory-stage6.mjs';

const id = (suffix) => `10000000-0000-0000-0000-00000000000${suffix}`;
const seasonId = id(1);
const archivedSeasonId = id(2);
const itemId = id(3);
const targetId = id(4);
const playerId = id(5);
const base = `/api/seasons/${seasonId}`;
const gallery = `${base}/gallery`;
const thumbnail = `${gallery}/${itemId}/thumbnail`;
const feed = `${base}/feed?limit=100`;
const comments = `${base}/feed/${targetId}/comments`;
const reactions = `${base}/feed/reactions?commandIds=${targetId}`;
const recap = `${base}/my-statistics`;
const allTime = '/api/me/statistics';
const hall = '/api/hall-of-fame';
const records = `${hall}/records`;
const nominations = `/api/seasons/${archivedSeasonId}/nominations`;
const time = '2026-01-01T12:00:00Z';
const json = (value, status = 200) => Response.json(value, { status });

// A complete 1 × 1 lossless WebP image.
const webp = Buffer.from('UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAAAfQ//73v/+BiOh/AAA=', 'base64');

function site() {
  const reactionRows = { [targetId]: [{ emoji: '👍', count: 2, selected: false }] };
  const pages = new Map([
    [
      gallery,
      {
        items: [
          {
            id: itemId,
            playerId,
            playerName: 'Player',
            caption: 'A demo gallery moment',
            createdAt: time,
            imageUrl: `${gallery}/${itemId}/image`,
            thumbnailUrl: thumbnail,
          },
        ],
      },
    ],
    [
      feed,
      {
        entries: [{ commandId: targetId, undone: false }],
        commentTargets: [targetId],
        reactions: reactionRows,
      },
    ],
    [
      comments,
      { comments: [{ id: id(6), author: 'Player', text: 'A demo comment', createdAt: time }] },
    ],
    [reactions, reactionRows],
    [recap, { seasonId, seasonName: 'Demo season', completedRuns: 2 }],
    [allTime, { seasonCount: 2, completedRuns: 3, totalHours: 22.25 }],
    [
      hall,
      [
        {
          seasonId: archivedSeasonId,
          seasonName: 'Archived season',
          winners: [{ playerId, name: 'Winner', points: 20 }],
        },
      ],
    ],
    [
      records,
      {
        longestGames: [
          {
            seasonId: archivedSeasonId,
            gameId: id(7),
            runId: id(8),
            title: 'Long game',
            hours: 20.25,
          },
        ],
        largestRolls: [
          {
            seasonId: archivedSeasonId,
            runId: id(8),
            playerId,
            name: 'Winner',
            sum: 12,
          },
        ],
      },
    ],
    [
      nominations,
      {
        automatic: [
          { nomination: 'mostHours', value: 20.25, winners: [{ playerId, name: 'Winner' }] },
        ],
        voted: [
          {
            nomination: 'bestReview',
            pollId: id(9),
            hasWinner: true,
            winner: { id: id(8), label: 'Long game' },
          },
        ],
      },
    ],
  ]);
  const overrides = new Map([
    [thumbnail, () => new Response(webp, { headers: { 'content-type': 'image/webp' } })],
  ]);
  const paths = [];
  return {
    pages,
    overrides,
    paths,
    call(path, init) {
      assert.equal(init?.method ?? 'GET', 'GET');
      paths.push(path);
      if (overrides.has(path)) return overrides.get(path)();
      assert.ok(pages.has(path), `Unexpected path ${path}`);
      return json(pages.get(path));
    },
  };
}

test('discovers all stage 6 load routes from real data through read-only requests', async () => {
  const demo = site();
  const workload = await assertStage6MemoryReads({ call: demo.call, seasonId });

  assert.equal(workload.itemId, itemId);
  assert.equal(workload.targetId, targetId);
  assert.equal(workload.archiveSeasonId, archivedSeasonId);
  assert.deepEqual(
    new Set(workload.pages),
    new Set([gallery, thumbnail, comments, reactions, recap, hall, records, allTime, nominations]),
  );
  assert.deepEqual(new Set(demo.paths), new Set([feed, ...workload.pages]));
});

test('postflight checks the same gallery item, discussion and archive even if discovery order changes', async () => {
  const demo = site();
  const workload = await assertStage6MemoryReads({ call: demo.call, seasonId });
  const after = site();
  after.pages.delete(feed);
  const newerArchiveId = id('a');
  after.pages.get(hall).unshift({
    seasonId: newerArchiveId,
    seasonName: 'Another archive',
    winners: [{ playerId, name: 'Winner', points: 24 }],
  });
  after.pages.get(records).longestGames.unshift({
    ...after.pages.get(records).longestGames[0],
    seasonId: newerArchiveId,
  });

  await assertStage6MemoryReads({ call: after.call, seasonId, workload });

  assert.deepEqual(new Set(after.paths), new Set(workload.pages));
  const replacement = after.pages.get(gallery).items[0];
  replacement.id = id(9);
  replacement.imageUrl = `${gallery}/${replacement.id}/image`;
  replacement.thumbnailUrl = `${gallery}/${replacement.id}/thumbnail`;
  await assert.rejects(
    () => assertStage6MemoryReads({ call: after.call, seasonId, workload }),
    /gallery moment/,
  );
});

test('rejects a malformed or empty season ID before making requests', async () => {
  for (const invalidId of ['../other-season', '00000000-0000-0000-0000-000000000000']) {
    const demo = site();
    await assert.rejects(
      () => assertStage6MemoryReads({ call: demo.call, seasonId: invalidId }),
      /season ID/,
    );
    assert.deepEqual(demo.paths, []);
  }
});

for (const path of [
  gallery,
  thumbnail,
  feed,
  comments,
  reactions,
  recap,
  allTime,
  hall,
  records,
  nominations,
]) {
  test(`${path} returning an HTTP failure aborts preflight`, async () => {
    const demo = site();
    demo.overrides.set(path, () => json({ code: 'feature.disabled' }, 409));

    await assert.rejects(() => assertStage6MemoryReads({ call: demo.call, seasonId }), /HTTP 409/);
    assert.ok(demo.paths.includes(path));
  });
}

const emptyResponses = [
  ['gallery with missing items', gallery, {}, /gallery moment/],
  ['gallery with no visible items', gallery, { items: [] }, /gallery moment/],
  [
    'feed without comment targets',
    feed,
    { entries: [{ commandId: targetId, undone: false }] },
    /discussed feed entry/,
  ],
  ['thread with no visible comments', comments, { comments: [] }, /visible comment/],
  [
    'thread with a comment missing its ID',
    comments,
    { comments: [{ text: 'Comment', author: 'Player', createdAt: time }] },
    /visible comment/,
  ],
  ['feed reactions that are disabled', reactions, {}, /feed reaction/],
  [
    'feed reactions without a vote',
    reactions,
    { [targetId]: [{ emoji: '👍', count: 0 }] },
    /feed reaction/,
  ],
  [
    'private recap without completions',
    recap,
    { seasonId, seasonName: 'Demo season', completedRuns: 0 },
    /private recap/,
  ],
  [
    'private recap of another season',
    recap,
    { seasonId: archivedSeasonId, seasonName: 'Archived season', completedRuns: 1 },
    /private recap/,
  ],
  [
    'all-time statistics showing only the current season',
    allTime,
    { seasonCount: 1, completedRuns: 2, totalHours: 15 },
    /all-time statistics/,
  ],
  [
    'all-time statistics without archived completions',
    allTime,
    { seasonCount: 2, completedRuns: 2, totalHours: 15 },
    /all-time statistics/,
  ],
  [
    'Hall of Fame without winners',
    hall,
    [{ seasonId: archivedSeasonId, seasonName: 'Archived season', winners: [] }],
    /archived season with winners/,
  ],
  ['Hall of Fame without records', records, { longestGames: [] }, /longest game/],
  [
    'record from a season without a winner',
    records,
    { longestGames: [{ seasonId, gameId: id(7), runId: id(8), title: 'Long game', hours: 20 }] },
    /longest game/,
  ],
  [
    'record without a valid run ID',
    records,
    {
      longestGames: [
        {
          seasonId: archivedSeasonId,
          gameId: id(7),
          runId: '../../run',
          title: 'Long game',
          hours: 20,
        },
      ],
    },
    /longest game/,
  ],
];

test('the Hall of Fame needs a valid largest roll as well as a longest game', async () => {
  const demo = site();
  const record = demo.pages.get(records);
  record.largestRolls[0].sum = 0;

  await assert.rejects(
    () => assertStage6MemoryReads({ call: demo.call, seasonId }),
    /largest roll/,
  );
});

test('the archive needs an automatic award and a visible voted winner', async () => {
  for (const hideAward of [
    (snapshot) => (snapshot.automatic = []),
    (snapshot) => (snapshot.voted[0].winner = null),
  ]) {
    const demo = site();
    hideAward(demo.pages.get(nominations));
    await assert.rejects(
      () => assertStage6MemoryReads({ call: demo.call, seasonId }),
      /archived nominations/,
    );
  }
});

for (const [name, path, body, expected] of emptyResponses) {
  test(`${name} cannot count as stage 6 load`, async () => {
    const demo = site();
    demo.pages.set(path, body);

    await assert.rejects(() => assertStage6MemoryReads({ call: demo.call, seasonId }), expected);
    assert.ok(demo.paths.includes(path));
  });
}

for (const [name, update] of [
  ['an invalid gallery item ID', (item) => (item.id = 'missing')],
  [
    'an external thumbnail URL',
    (item) => (item.thumbnailUrl = `https://example.invalid${thumbnail}`),
  ],
]) {
  test(`${name} cannot select a thumbnail to read`, async () => {
    const demo = site();
    update(demo.pages.get(gallery).items[0]);

    await assert.rejects(
      () => assertStage6MemoryReads({ call: demo.call, seasonId }),
      /gallery moment/,
    );
    assert.ok(!demo.paths.includes(thumbnail));
  });
}

test('rejects an invalid discussion ID before forming the URL', async () => {
  const demo = site();
  demo.pages.set(feed, {
    entries: [{ commandId: '../elsewhere', undone: false }],
    commentTargets: ['../elsewhere'],
    reactions: { '../elsewhere': [{ emoji: '👍', count: 2 }] },
  });

  await assert.rejects(
    () => assertStage6MemoryReads({ call: demo.call, seasonId }),
    /discussed feed entry/,
  );
});

for (const [name, body, contentType] of [
  ['empty', null, 'image/webp'],
  ['a JSON disguised as WebP', '{}', 'image/webp'],
  ['a WebP with the wrong media type', webp, 'text/plain'],
]) {
  test(`${name} thumbnail cannot count as an image response`, async () => {
    const demo = site();
    demo.overrides.set(
      thumbnail,
      () => new Response(body, { headers: { 'content-type': contentType } }),
    );

    await assert.rejects(() => assertStage6MemoryReads({ call: demo.call, seasonId }), /WebP/);
  });
}
