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
const gameId = '44000000-0000-0000-0000-000000000001';
const playerId = '55000000-0000-0000-0000-000000000001';
const runId = '66000000-0000-0000-0000-000000000001';
const kitInstanceId = '66000000-0000-0000-0000-000000000009';
const effectId = '66000000-0000-0000-0000-000000000010';
const trialId = 'memory-trial';
const kitId = 'memory-event-kit';
const completedAt = new Date(Date.now() - 2 * 86_400_000).toISOString();
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
    JSON.parse(readFileSync(new URL('../content/events/karaoke.json', import.meta.url))),
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
        check: 'manual',
        rewardCoins: 10,
        startsAt: now,
        endsAt: end,
        completedBy: [{ playerId, name: 'Первый игрок' }],
      },
    ],
  };
}

// A tiny HTTP state machine, not a mock of helper internals: writes change the state read by GET.
function completedRun(overrides = {}) {
  return {
    runId,
    seasonId,
    seasonName: 'Демо-сезон',
    playerId,
    userId: '77000000-0000-0000-0000-000000000001',
    playerName: 'Первый игрок',
    status: 'completed',
    difficulty: 'normal',
    hours: 6,
    completedAt,
    rating: null,
    reviewText: null,
    token: 1,
    ...overrides,
  };
}

function memorySite({ reject, runs, hiddenRead } = {}) {
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
    reads: [],
    marks: [],
    completionReads: [],
    challengeDefinitions: new Map(),
    kitGranted: false,
    kitUsed: false,
    irlProof: null,
    existingRuns: runs ?? [
      completedRun({
        runId: '66000000-0000-0000-0000-000000000002',
        seasonId: 'de300000-0000-0000-0000-000000000002',
      }),
      completedRun({
        runId: '66000000-0000-0000-0000-000000000003',
        status: 'playing',
        completedAt: null,
      }),
      completedRun(),
    ],
  };
  const initialChoiceCount = state.ruleset.roll.choiceCount;
  const call =
    (who) =>
    async (inputPath, init = {}) => {
      const path = inputPath.replace(/\/$/, '');
      const method = init.method ?? 'GET';
      const body = init.body ? JSON.parse(init.body) : undefined;
      if (method === 'GET') state.reads.push(path);
      if (reject?.({ who, path, method, body })) {
        state.refusals++;
        return json({ code: 'test.refused', title: 'The setup command was refused.' }, 409);
      }
      if (method === 'GET' && path === season) {
        assert.equal(who, 'player01');
        return json({
          id: seasonId,
          status: 'active',
          me: {
            playerId,
            phase: 'idle',
            eventsEnabled: state.ruleset.features.events,
            waitingConditions:
              state.kitUsed && hiddenRead !== 'condition'
                ? [{ objectId: trialId, difficultyAtLeast: 'hard' }]
                : [],
            waitingEvents:
              state.kitUsed && hiddenRead !== 'throw'
                ? [{ objectId: trialId, waitingForRoll: false, waitingForThrow: true }]
                : [],
            manualEffects: state.kitUsed
              ? [{ id: effectId, objectId: 'karaoke', irl: true, irlProof: state.irlProof }]
              : [],
          },
        });
      }
      if (method === 'GET' && path === `${season}/economy`) {
        assert.equal(who, 'player01');
        return json({
          inventoryLimit: 2,
          items:
            state.kitGranted && !state.kitUsed
              ? [{ instanceId: kitInstanceId, objectId: kitId, kind: 'item', usable: true }]
              : [],
        });
      }
      if (method === 'POST' && path === `${adminSeason}/players/${playerId}/inventory`) {
        assert.equal(who, 'admin');
        assert.ok(body.commandId);
        assert.ok(body.comment);
        assert.equal(body.giveObjectId, kitId);
        const kit = state.published.objects.find((object) => object.id === body.giveObjectId);
        assert.equal(kit.kind, 'item');
        assert.deepEqual(
          kit.effect.actions.map(({ type }) => type),
          ['drawEvent', 'drawEvent'],
        );
        assert.deepEqual(
          kit.effect.actions.map(
            ({ deck }) => state.published.wheels.find(({ id }) => id === deck).entries,
          ),
          [[{ objectId: trialId, weight: 1 }], [{ objectId: 'karaoke', weight: 1 }]],
        );
        state.kitGranted = true;
        state.writes.push('grant');
        return json({ duplicate: false, events: [] });
      }
      if (method === 'POST' && path === `${season}/items/${kitInstanceId}/use`) {
        assert.equal(who, 'player01');
        assert.ok(body.commandId);
        assert.ok(state.kitGranted);
        state.kitUsed = true;
        state.writes.push('use');
        return json({ duplicate: false, events: [] });
      }
      if (method === 'POST' && path === `${season}/effects/${effectId}/irl-proof`) {
        assert.equal(who, 'player01');
        assert.ok(body.commandId);
        assert.equal(new URL(body.link).protocol, 'https:');
        assert.ok(state.kitUsed);
        state.irlProof = {
          status: 'pending',
          link: body.link,
          files: [],
          submittedAt: new Date().toISOString(),
        };
        state.writes.push('submitIrlProof');
        return json({ duplicate: false, events: [] });
      }
      if (method === 'GET' && path === `${adminSeason}/irl-proofs`) {
        assert.equal(who, 'admin');
        return json(
          state.irlProof && hiddenRead !== 'queue'
            ? [
                {
                  effectId,
                  playerId: hiddenRead === 'different owner' ? 'other-player' : playerId,
                  objectId: 'karaoke',
                  name: 'Караоке',
                  link: state.irlProof.link,
                  submittedAt: state.irlProof.submittedAt,
                },
              ]
            : [],
        );
      }
      if (method === 'GET' && path === `${adminSeason}/effects`) {
        assert.equal(who, 'admin');
        return json(
          state.kitUsed && hiddenRead !== 'effects'
            ? [
                {
                  id: effectId,
                  playerId,
                  objectId: 'karaoke',
                  irl: true,
                },
              ]
            : [],
        );
      }
      if (method === 'GET' && path === `${season}/rules`) {
        return json({ version: state.version, ruleset: state.ruleset, history: [] });
      }
      if (method === 'GET' && path === `${season}/games`) {
        return json(
          state.existingRuns.length
            ? [
                {
                  gameId,
                  taken: 'completedInSeason',
                  takenBy: 'Первый игрок',
                  completedAt,
                  marks: [],
                  excludedForMe: null,
                  offered: false,
                },
              ]
            : [],
        );
      }
      if (method === 'GET' && path === `/api/pool/${gameId}/runs`) {
        return json(state.existingRuns);
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
        const challenge = sampleReads()['weekly-challenges'][0];
        challenge.name = body.challenge.name;
        challenge.description = body.challenge.description;
        challenge.check = body.challenge.check;
        challenge.startsAt = body.startsAt;
        challenge.endsAt = new Date(
          new Date(body.startsAt).getTime() +
            state.ruleset.weeklyChallenge.durationDays * 86_400_000,
        ).toISOString();
        challenge.completedBy = [];
        state.challenges.push(challenge);
        state.challengeDefinitions.set(challenge.id, copy(body.challenge));
        state.writes.push('challenge');
        return json({ duplicate: false, events: [{ type: 'weekly-challenge-set' }] });
      }
      const mark = new RegExp(`^${adminSeason}/weekly-challenges/([^/]+)/mark$`).exec(path);
      if (method === 'POST' && mark) {
        assert.equal(who, 'admin');
        assert.ok(body.commandId);
        const challenge = state.challenges.find((item) => item.id === mark[1]);
        assert.ok(challenge, 'The mark names a challenge created by the setup.');
        assert.equal(state.challengeDefinitions.get(challenge.id).check, 'manual');
        const run = state.existingRuns.find(
          (item) =>
            item.runId === body.runId &&
            item.playerId === body.playerId &&
            item.seasonId === seasonId &&
            item.status === 'completed' &&
            item.completedAt !== null,
        );
        assert.ok(run, 'The mark names a real completed run of this season and its owner.');
        const finished = Date.parse(run.completedAt);
        assert.ok(
          finished >= Date.parse(challenge.startsAt),
          'The challenge starts by the completion.',
        );
        assert.ok(
          finished < Date.parse(challenge.endsAt),
          'The completion precedes the challenge deadline.',
        );
        challenge.completedBy.push({ playerId: run.playerId, name: run.playerName });
        state.marks.push({ playerId: body.playerId, runId: body.runId, challengeId: challenge.id });
        state.writes.push('mark');
        return json({ duplicate: false, events: [{ type: 'weekly-challenge-completed' }] });
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
      if (method === 'GET' && path === `${season}/weekly-challenges`) {
        state.completionReads.push(copy(state.challenges.flatMap((item) => item.completedBy)));
        return json(state.challenges);
      }
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

test('the memory setup creates waiting events and verifies the same pending proof on the player and admin views', async () => {
  const site = memorySite();

  await prepareStage5MemoryWorkload({ ...site, seasonId, content });

  assert.deepEqual(
    site.state.writes.filter((write) => ['grant', 'use', 'submitIrlProof'].includes(write)),
    ['grant', 'use', 'submitIrlProof'],
  );
  assert.ok(site.state.reads.includes(season));
  assert.ok(site.state.reads.includes(`${adminSeason}/irl-proofs`));
  assert.ok(site.state.reads.includes(`${adminSeason}/effects`));
  assert.equal(site.state.irlProof.status, 'pending');
});

for (const hiddenRead of ['condition', 'throw', 'queue', 'different owner', 'effects']) {
  test(`an event workload with a missing or inconsistent ${hiddenRead} fails preparation`, async () => {
    await assert.rejects(() =>
      prepareStage5MemoryWorkload({ ...memorySite({ hiddenRead }), seasonId, content }),
    );
  });
}

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
  ['marking a weekly challenge completion', ({ path }) => path.endsWith('/mark')],
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

test('the setup marks a real completed run inside a manual challenge window and reads its completion', async () => {
  const site = memorySite();

  await prepareStage5MemoryWorkload({ ...site, seasonId, content });

  assert.ok(site.state.reads.includes(`${season}/games`));
  assert.ok(site.state.reads.includes(`/api/pool/${gameId}/runs`));
  assert.ok(site.state.marks.some((mark) => mark.playerId === playerId && mark.runId === runId));
  assert.ok(
    site.state.completionReads.some((read) => read.some((item) => item.playerId === playerId)),
  );
  const marked = site.state.challenges.find((challenge) =>
    site.state.marks.some((mark) => mark.challengeId === challenge.id),
  );
  assert.equal(marked.check, 'manual');
  assert.ok(Date.parse(marked.startsAt) <= Date.parse(completedAt));
  assert.ok(Date.parse(completedAt) < Date.parse(marked.endsAt));
});

for (const [name, runs] of [
  ['no runs', []],
  ['only playing runs', [completedRun({ status: 'playing', completedAt: null })]],
  ['only dropped runs', [completedRun({ status: 'dropped' })]],
  ['only rejected runs', [completedRun({ status: 'rejected' })]],
  [
    'only runs of another season',
    [completedRun({ seasonId: 'de300000-0000-0000-0000-000000000002' })],
  ],
  ['a completed run without its completion time', [completedRun({ completedAt: null })]],
]) {
  test(`${name} cannot prepare a workload that measures weekly challenge completions`, async () => {
    const site = memorySite({ runs });
    await assert.rejects(() => prepareStage5MemoryWorkload({ ...site, seasonId, content }));
    assert.ok(site.state.reads.includes(`${season}/games`));
    assert.equal(site.state.marks.length, 0);
  });
}

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

for (const [name, completedBy] of [
  ['an empty completion list', []],
  ['a completion without its player id', [{ name: 'Первый игрок' }]],
  ['a string instead of a completion record', [playerId]],
  ['a null completion record', [null]],
]) {
  test(`weekly-challenges returning ${name} cannot claim to read completion rows`, async () => {
    const reads = sampleReads();
    reads['weekly-challenges'][0].completedBy = completedBy;
    let reached = false;
    await assert.rejects(() =>
      assertStage5MemoryReads({
        seasonId,
        call: async (path) => {
          if (path === `${season}/weekly-challenges`) reached = true;
          return json(reads[path.split('/').at(-1)]);
        },
      }),
    );
    assert.equal(reached, true);
  });
}
