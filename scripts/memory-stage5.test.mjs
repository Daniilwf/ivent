import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { assertStage5MemoryReads, prepareStage5MemoryWorkload } from './memory-stage5.mjs';

// The benchmark must load the stage 5 tables, not accept feature.disabled as a measurement.
// Written from the public HTTP contract and the task 5.19 acceptance criterion before the helper.
const seasonId = 'de300000-0000-0000-0000-000000000001';
const season = `/api/seasons/${seasonId}`;
const adminSeason = `/api/admin/seasons/${seasonId}`;
const stage5Flags = ['items', 'events', 'polls', 'globalEvents', 'weeklyChallenge'];
const content = {
  objects: [
    {
      id: 'memory-rain',
      kind: 'event',
      name: 'Монетный дождь',
      description: 'Всем по монетке.',
      rarity: 'common',
      price: null,
      hostile: false,
      effect: {
        target: { selector: 'self' },
        actions: [{ type: 'changeResource', resource: 'coins', amount: 1 }],
      },
    },
  ],
  wheels: [],
};
const json = (body, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
const copy = (value) => structuredClone(value);

function sampleReads() {
  const now = new Date().toISOString();
  const end = new Date(Date.now() + 86_400_000).toISOString();
  return {
    polls: [
      {
        id: '11000000-0000-0000-0000-000000000001',
        question: 'Что играем?',
        options: [
          { id: 'Длинное', label: 'Длинное' },
          { id: 'Короткое', label: 'Короткое' },
        ],
        voters: 'players',
        anonymous: false,
        status: 'open',
        startedAt: now,
        closesAt: end,
        counts: [
          { optionId: 'Длинное', votes: 1 },
          { optionId: 'Короткое', votes: 0 },
        ],
        myVote: 'Длинное',
        result: null,
        source: 'admin',
      },
    ],
    'global-events': [
      {
        id: '22000000-0000-0000-0000-000000000001',
        objectId: 'memory-rain',
        name: 'Монетный дождь',
        description: 'Всем по монетке.',
        status: 'active',
        startsAt: now,
        endsAt: end,
        goal: { metric: 'runs', target: 100, progress: 0, reached: false },
      },
    ],
    'weekly-challenges': [
      {
        id: '33000000-0000-0000-0000-000000000001',
        name: 'Неделя игр',
        description: 'Пройди игру.',
        check: 'auto',
        rewardCoins: 10,
        startsAt: now,
        endsAt: end,
        completedBy: [],
      },
    ],
  };
}

// A tiny HTTP state machine, not a mock of helper internals: writes change the state read by GET.
function memorySite({ reject } = {}) {
  const heldItem = {
    id: 'held-orange',
    kind: 'item',
    name: 'Апельсин',
    description: 'Шаг вперёд.',
    rarity: 'common',
    price: 5,
    hostile: false,
    effect: { target: { selector: 'self' }, actions: [{ type: 'move', steps: 1 }] },
  };
  const state = {
    ruleset: JSON.parse(readFileSync(new URL('../docs/ruleset.default.json', import.meta.url))),
    version: 1,
    contentVersion: 2,
    published: null,
    polls: [],
    globalEvents: [],
    challenges: [],
    votes: new Map(),
    writes: [],
    refusals: 0,
  };
  const initialChoiceCount = state.ruleset.roll.choiceCount;
  const call =
    (who) =>
    async (inputPath, init = {}) => {
      const path = inputPath.replace(/\/$/, '');
      const method = init.method ?? 'GET';
      const body = init.body ? JSON.parse(init.body) : undefined;
      if (reject?.({ who, path, method, body })) {
        state.refusals++;
        return json({ code: 'test.refused', title: 'The setup command was refused.' }, 409);
      }
      if (method === 'GET' && path === `${season}/rules`) {
        return json({ version: state.version, ruleset: state.ruleset, history: [] });
      }
      if (method === 'GET' && path === `${adminSeason}/content`) {
        return json({
          version: state.contentVersion,
          pack: state.published ?? { objects: [heldItem], wheels: [] },
          itemsEnabled: state.ruleset.features.items,
          eventsEnabled: state.ruleset.features.events,
          status: 'active',
          deleted: [],
          history: [],
        });
      }
      if (method === 'PUT' && path === `${adminSeason}/rules`) {
        assert.equal(who, 'admin');
        assert.equal(body.expectedVersion, state.version);
        assert.ok(body.commandId);
        state.ruleset = copy(body.ruleset);
        state.version++;
        state.writes.push('rules');
        return json({ version: state.version, warnings: [] });
      }
      if (method === 'POST' && path === `${adminSeason}/content/publish`) {
        assert.equal(who, 'admin');
        assert.equal(state.ruleset.features.items, true);
        assert.equal(state.ruleset.features.events, true);
        assert.equal(body.expectedVersion, state.contentVersion);
        assert.ok(body.commandId);
        state.published = copy(body.pack);
        state.contentVersion++;
        state.writes.push('content');
        return json({ duplicate: false, events: [{ type: 'content-published' }] });
      }
      if (method === 'POST' && path === `${adminSeason}/polls`) {
        assert.equal(who, 'admin');
        assert.equal(state.ruleset.features.polls, true);
        assert.ok(body.commandId);
        assert.ok(body.poll.options.list.length >= 2);
        const card = sampleReads().polls[0];
        card.question = body.poll.question;
        card.options = body.poll.options.list.map((id) => ({ id, label: id }));
        card.counts = card.options.map(({ id }) => ({ optionId: id, votes: 0 }));
        card.myVote = null;
        state.polls.push(card);
        state.writes.push('poll');
        return json({ duplicate: false, events: [{ type: 'poll-started' }] });
      }
      if (method === 'POST' && path === `${adminSeason}/global-events`) {
        assert.equal(who, 'admin');
        assert.equal(state.ruleset.features.globalEvents, true);
        assert.ok(state.published?.objects.some((object) => object.id === body.objectId));
        assert.ok(body.commandId);
        assert.ok(body.goal?.target > 0);
        const event = sampleReads()['global-events'][0];
        event.objectId = body.objectId;
        event.goal = { ...body.goal, progress: 0, reached: false };
        state.globalEvents.push(event);
        state.writes.push('globalEvent');
        return json({ duplicate: false, events: [{ type: 'global-event-launched' }] });
      }
      if (method === 'POST' && path === `${adminSeason}/weekly-challenges`) {
        assert.equal(who, 'admin');
        assert.equal(state.ruleset.features.weeklyChallenge, true);
        assert.ok(body.commandId);
        assert.ok(body.challenge.condition);
        assert.ok(body.startsAt);
        state.challenges.push(sampleReads()['weekly-challenges'][0]);
        state.writes.push('challenge');
        return json({ duplicate: false, events: [{ type: 'weekly-challenge-set' }] });
      }
      const vote = new RegExp(`^${season}/polls/([^/]+)/vote$`).exec(path);
      if (method === 'POST' && vote) {
        assert.notEqual(who, 'admin');
        assert.ok(body.commandId);
        const poll = state.polls.find((item) => item.id === vote[1]);
        assert.ok(poll, 'A player votes on a real poll created by the setup.');
        assert.ok(poll.options.some((option) => option.id === body.optionId));
        state.votes.set(who, body.optionId);
        poll.counts = poll.options.map(({ id }) => ({
          optionId: id,
          votes: [...state.votes.values()].filter((optionId) => optionId === id).length,
        }));
        state.writes.push('vote');
        return json({ duplicate: false, events: [{ type: 'vote-cast' }] });
      }
      if (method === 'GET' && path === `${season}/polls`) {
        return json(state.polls.map((poll) => ({ ...poll, myVote: state.votes.get(who) ?? null })));
      }
      if (method === 'GET' && path.startsWith(`${season}/polls/`)) {
        const id = path.split('/').at(-1);
        const poll = state.polls.find((item) => item.id === id);
        return poll ? json({ ...poll, myVote: state.votes.get(who) ?? null }) : json({}, 404);
      }
      if (method === 'GET' && path === `${season}/global-events`) return json(state.globalEvents);
      if (method === 'GET' && path === `${season}/weekly-challenges`) return json(state.challenges);
      throw new Error(`Unexpected request ${who} ${method} ${path}`);
    };
  return {
    state,
    initialChoiceCount,
    admin: call('admin'),
    players: [call('player01'), call('player02')],
  };
}

test('the setup enables the mechanics and creates projected data with real player votes', async () => {
  const site = memorySite();
  const original = copy(content);

  await prepareStage5MemoryWorkload({ ...site, seasonId, content });

  for (const flag of stage5Flags) assert.equal(site.state.ruleset.features[flag], true, flag);
  assert.equal(site.state.ruleset.roll.choiceCount, site.initialChoiceCount);
  assert.deepEqual(
    content,
    original,
    'The repository content supplied to the helper is not mutated.',
  );
  assert.ok(site.state.published.objects.some((object) => object.id === 'memory-rain'));
  assert.ok(
    site.state.published.objects.some((object) => object.id === 'held-orange'),
    'Adding benchmark events preserves the already published demo items.',
  );
  assert.ok(site.state.polls.length > 0);
  assert.ok(site.state.votes.size > 0);
  assert.ok(site.state.polls.some((poll) => poll.counts.some((count) => count.votes > 0)));
  assert.ok(
    site.state.globalEvents.some((event) => event.status === 'active' && event.goal.target > 0),
  );
  assert.ok(site.state.challenges.length > 0);
  await assertStage5MemoryReads({ call: site.players[0], seasonId });
});

for (const [name, match] of [
  ['saving enabled rules', ({ path, method }) => method === 'PUT' && path.endsWith('/rules')],
  ['publishing the event content', ({ path }) => path.endsWith('/content/publish')],
  ['starting a poll', ({ path, method }) => method === 'POST' && path === `${adminSeason}/polls`],
  ['casting a player vote', ({ path }) => path.endsWith('/vote')],
  [
    'launching a global event',
    ({ path, method }) => method === 'POST' && path === `${adminSeason}/global-events`,
  ],
  [
    'setting a weekly challenge',
    ({ path, method }) => method === 'POST' && path === `${adminSeason}/weekly-challenges`,
  ],
]) {
  test(`a refused command while ${name} fails the workload preparation`, async () => {
    const site = memorySite({ reject: match });
    await assert.rejects(() => prepareStage5MemoryWorkload({ ...site, seasonId, content }));
    assert.ok(site.state.refusals > 0, 'The setup reached the command whose refusal is tested.');
  });
}

test('a workload without players cannot claim to measure player votes', async () => {
  const site = memorySite();
  await assert.rejects(() =>
    prepareStage5MemoryWorkload({ ...site, players: [], seasonId, content }),
  );
});

test('a populated response from all stage 5 tables is accepted', async () => {
  const reads = sampleReads();
  const seen = new Set();
  await assertStage5MemoryReads({
    seasonId,
    call: async (path) => {
      seen.add(path);
      return json(reads[path.split('/').at(-1)]);
    },
  });
  for (const page of Object.keys(reads)) assert.ok(seen.has(`${season}/${page}`));
});

for (const page of ['polls', 'global-events', 'weekly-challenges']) {
  for (const [name, response] of [
    ['feature.disabled', () => json({ code: 'feature.disabled' }, 409)],
    ['an empty list', () => json([])],
    ['an unexpected response shape', () => json({ items: [] })],
    ['an unrelated object in the list', () => json([{ id: 'unrelated' }])],
  ]) {
    test(`${page} returning ${name} cannot pass a stage 5 measurement`, async () => {
      const reads = sampleReads();
      let reached = false;
      await assert.rejects(() =>
        assertStage5MemoryReads({
          seasonId,
          call: async (path) => {
            if (path === `${season}/${page}`) {
              reached = true;
              return response();
            }
            return json(reads[path.split('/').at(-1)]);
          },
        }),
      );
      assert.equal(reached, true, 'The assertion read the endpoint whose weak answer is tested.');
    });
  }
}
