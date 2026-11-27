// Prepare only the disposable benchmark season, through the same API and command queue as the admin.
import { randomUUID } from 'node:crypto';

async function read(call, path, init) {
  const response = await call(path, init);
  if (!response.ok)
    throw new Error(`Memory workload: ${init?.method ?? 'GET'} ${path}: HTTP ${response.status}`);
  return response.json();
}

const write = (call, path, body, method = 'POST') =>
  read(call, path, {
    method,
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ commandId: randomUUID(), ...body }),
  });

function list(value, name) {
  if (!Array.isArray(value) || value.length === 0)
    throw new Error(`Memory workload: ${name} must contain projected data.`);
  return value;
}

const text = (value) => typeof value === 'string' && value.length > 0;
const nonnegative = (value) => Number.isFinite(value) && value >= 0;

const trialId = 'memory-trial';
const kitId = 'memory-event-kit';
const trialDeckId = 'memory-trial-deck';
const irlDeckId = 'memory-irl-deck';
const proofLink = 'https://example.invalid/ivent-memory-proof';

const trial = {
  id: trialId,
  kind: 'event',
  name: 'Испытание для замера памяти',
  description:
    'Следующую игру пройди на сложной сложности или выше. В броске на один кубик больше.',
  rarity: 'common',
  price: null,
  hostile: false,
  effect: {
    target: { selector: 'self' },
    actions: [
      { type: 'modifyNextRoll', runCondition: { difficultyAtLeast: 'hard' } },
      { type: 'modifyDice', when: 'next', stage: 'count', value: 1 },
    ],
  },
};

const kit = {
  id: kitId,
  kind: 'item',
  name: 'Набор для замера памяти',
  description: 'Разыграй испытание и IRL-ивент.',
  rarity: 'common',
  price: 1,
  window: 'anytime',
  hostile: false,
  stackable: false,
  effect: {
    target: { selector: 'self' },
    actions: [
      { type: 'drawEvent', deck: trialDeckId },
      { type: 'drawEvent', deck: irlDeckId },
    ],
  },
};

/** Verify the same live proof and the waiting event before and after the load. The link is read as data. */
export async function assertIrlMemoryReads({
  admin,
  player,
  seasonId,
  playerId,
  effectId,
  objectId,
}) {
  const base = `/api/seasons/${seasonId}`;
  const adminBase = `/api/admin/seasons/${seasonId}`;
  const { me } = await read(player, base);
  if (me?.eventsEnabled !== true || me.playerId !== playerId)
    throw new Error('Memory workload: the proof owner has no active events view.');
  if (
    !me.waitingConditions?.some(
      (row) => row.objectId === trialId && row.difficultyAtLeast === 'hard',
    )
  )
    throw new Error('Memory workload: the player has no waiting condition from the trial event.');
  if (
    !me.waitingEvents?.some(
      (row) =>
        row.objectId === trialId && row.waitingForRoll === false && row.waitingForThrow === true,
    )
  )
    throw new Error('Memory workload: the player has no waiting throw from the trial event.');
  const effect = me.manualEffects?.find((row) => row.id === effectId);
  if (
    effect?.irl !== true ||
    effect.objectId !== objectId ||
    effect.irlProof?.status !== 'pending' ||
    effect.irlProof.link !== proofLink ||
    !Number.isFinite(Date.parse(effect.irlProof.submittedAt))
  )
    throw new Error('Memory workload: the owner has no matching pending IRL proof.');

  const proofs = list(await read(admin, `${adminBase}/irl-proofs`), 'IRL proof queue');
  if (
    !proofs.some(
      (row) =>
        row.effectId === effectId &&
        row.playerId === playerId &&
        row.objectId === objectId &&
        row.link === proofLink &&
        row.submittedAt === effect.irlProof.submittedAt,
    )
  )
    throw new Error('Memory workload: the admin queue has no matching pending IRL proof.');

  const effects = list(await read(admin, `${adminBase}/effects`), 'admin effects');
  if (
    !effects.some(
      (row) =>
        row.id === effectId &&
        row.playerId === playerId &&
        row.objectId === objectId &&
        row.irl === true,
    )
  )
    throw new Error('Memory workload: the admin effects view has no matching IRL effect.');
}

async function prepareIrlProof(admin, players, seasonId, objectId) {
  const base = `/api/seasons/${seasonId}`;
  const adminBase = `/api/admin/seasons/${seasonId}`;
  for (const [playerIndex, player] of players.entries()) {
    const before = await read(player, base);
    const playerId = before.me?.playerId;
    if (
      before.status !== 'active' ||
      !text(playerId) ||
      before.me.finish?.order === 1 ||
      before.me.choice?.kind === 'branch'
    )
      continue;
    const economy = await read(player, `${base}/economy`);
    const itemCount = economy.items?.filter((item) => item.kind === 'item').length ?? 0;
    if (itemCount >= economy.inventoryLimit) continue;

    await write(admin, `${adminBase}/players/${playerId}/inventory`, {
      giveObjectId: kitId,
      comment: 'Подготовка замера памяти этапа 5',
    });
    const inventory = await read(player, `${base}/economy`);
    const instance = inventory.items?.find((item) => item.objectId === kitId && item.usable);
    if (!text(instance?.instanceId))
      throw new Error('Memory workload: the granted event kit cannot be used.');
    await write(player, `${base}/items/${instance.instanceId}/use`, {});

    const previous = new Set(before.me.manualEffects.map((effect) => effect.id));
    const after = await read(player, base);
    const effect = after.me?.manualEffects.find(
      (row) => row.irl && row.objectId === objectId && !previous.has(row.id),
    );
    if (!text(effect?.id)) throw new Error('Memory workload: the kit did not draw an IRL effect.');
    await write(player, `${base}/effects/${effect.id}/irl-proof`, { link: proofLink });
    const proof = { playerIndex, playerId, effectId: effect.id, objectId };
    await assertIrlMemoryReads({ admin, player, seasonId, ...proof });
    return proof;
  }
  throw new Error('Memory workload: no eligible demo player has room for the event kit.');
}

/** A disabled mechanic, an empty table, or an error is never evidence of stage 5 memory usage. */
export async function assertStage5MemoryReads({ call, seasonId }) {
  const base = `/api/seasons/${seasonId}`;
  const polls = list(await read(call, `${base}/polls`), 'polls');
  if (
    !polls.some(
      (poll) =>
        text(poll.id) &&
        text(poll.question) &&
        poll.status === 'open' &&
        Array.isArray(poll.options) &&
        poll.options.length >= 2 &&
        poll.options.every((option) => text(option.id) && text(option.label)) &&
        Array.isArray(poll.counts) &&
        poll.counts.length === poll.options.length &&
        poll.counts.every(
          (count) =>
            poll.options.some((option) => option.id === count.optionId) &&
            Number.isInteger(count.votes) &&
            count.votes >= 0,
        ) &&
        poll.counts.some((count) => count.votes > 0),
    )
  )
    throw new Error('Memory workload: no open poll with options and player votes.');

  const events = list(await read(call, `${base}/global-events`), 'global-events');
  if (
    !events.some(
      (event) =>
        text(event.id) &&
        text(event.objectId) &&
        text(event.name) &&
        event.status === 'active' &&
        ['hours', 'runs'].includes(event.goal?.metric) &&
        Number.isFinite(event.goal.target) &&
        event.goal.target > 0 &&
        nonnegative(event.goal.progress) &&
        typeof event.goal.reached === 'boolean',
    )
  )
    throw new Error('Memory workload: no active global event with a goal.');

  const challenges = list(await read(call, `${base}/weekly-challenges`), 'weekly-challenges');
  if (
    !challenges.some(
      (challenge) =>
        text(challenge.id) &&
        text(challenge.name) &&
        ['auto', 'manual'].includes(challenge.check) &&
        nonnegative(challenge.rewardCoins) &&
        Number.isFinite(Date.parse(challenge.startsAt)) &&
        Date.parse(challenge.endsAt) > Date.parse(challenge.startsAt) &&
        Array.isArray(challenge.completedBy) &&
        challenge.completedBy.length > 0 &&
        challenge.completedBy.every((player) => text(player?.playerId) && text(player?.name)),
    )
  )
    throw new Error('Memory workload: no weekly challenge with real completions.');
  return { polls, events, challenges };
}

/** Keep the demo's pool, history, rules and held items; add stage 5 records with real player votes. */
export async function prepareStage5MemoryWorkload({ admin, players, seasonId, content }) {
  if (!Array.isArray(players) || players.length === 0)
    throw new Error('Memory workload: players are required.');
  const event = content.objects.find((object) => object.kind === 'event' && !object.manual);
  if (!event) throw new Error('Memory workload: an automatic event is required.');
  const irlEvent = content.objects.find(
    (object) => object.kind === 'event' && object.manual && object.proof === 'media',
  );
  if (!irlEvent) throw new Error('Memory workload: an IRL event is required.');
  const base = `/api/seasons/${seasonId}`;
  const adminBase = `/api/admin/seasons/${seasonId}`;
  const rules = await read(admin, `${base}/rules`);
  const ruleset = structuredClone(rules.ruleset);
  for (const name of ['events', 'polls', 'globalEvents', 'weeklyChallenge']) {
    if (!ruleset[name]) throw new Error(`Memory workload: missing ${name} rules.`);
  }
  Object.assign(ruleset.features, {
    items: true,
    events: true,
    polls: true,
    globalEvents: true,
    weeklyChallenge: true,
  });
  await write(admin, `${adminBase}/rules`, { expectedVersion: rules.version, ruleset }, 'PUT');

  const current = await read(admin, `${adminBase}/content`);
  const merge = (before, added) => [
    ...new Map([...before, ...added].map((item) => [item.id, item])).values(),
  ];
  await write(admin, `${adminBase}/content/publish`, {
    expectedVersion: current.version,
    pack: {
      objects: merge(current.pack.objects, [...content.objects, trial, kit]),
      wheels: merge(current.pack.wheels, [
        ...content.wheels,
        {
          id: trialDeckId,
          name: 'Испытание для замера',
          deck: 'bad',
          entries: [{ objectId: trialId, weight: 1 }],
        },
        {
          id: irlDeckId,
          name: 'IRL-ивент для замера',
          deck: 'bad',
          entries: [{ objectId: irlEvent.id, weight: 1 }],
        },
      ]),
    },
    comment: 'Контент временного замера памяти этапа 5',
  });

  const previous = new Set((await read(admin, `${base}/polls`)).map((poll) => poll.id));
  await write(admin, `${adminBase}/polls`, {
    poll: {
      question: 'Что сыграть после замера памяти?',
      options: { list: ['Короткая игра', 'Длинная игра'] },
      voters: 'players',
      anonymous: false,
      showLiveCounts: true,
      closesInHours: 24,
    },
  });
  const poll = list(await read(admin, `${base}/polls`), 'polls').find(
    (item) => !previous.has(item.id),
  );
  if (!poll || !poll.options?.length)
    throw new Error('Memory workload: the new poll was not projected.');
  await Promise.all(
    players.map((call, index) =>
      write(call, `${base}/polls/${poll.id}/vote`, {
        optionId: poll.options[index % poll.options.length].id,
      }),
    ),
  );

  await write(admin, `${adminBase}/global-events`, {
    objectId: event.id,
    durationHours: 24,
    goal: { metric: 'runs', target: 100 },
  });
  const games = list(await read(admin, `${base}/games`), 'season games');
  let completed;
  for (const game of games
    .filter((game) => text(game.gameId) && Number.isFinite(Date.parse(game.completedAt)))
    .sort((a, b) => Date.parse(b.completedAt) - Date.parse(a.completedAt))) {
    const runs = await read(admin, `/api/pool/${game.gameId}/runs`);
    if (!Array.isArray(runs)) throw new Error('Memory workload: invalid game run history.');
    completed = runs.find(
      (run) =>
        run.seasonId === seasonId &&
        run.status === 'completed' &&
        text(run.runId) &&
        text(run.playerId) &&
        Number.isFinite(Date.parse(run.completedAt)),
    );
    if (completed) break;
  }
  if (!completed) throw new Error('Memory workload: no completed run in the benchmark season.');
  const oldChallenges = new Set(
    (await read(admin, `${base}/weekly-challenges`)).map((challenge) => challenge.id),
  );
  await write(admin, `${adminBase}/weekly-challenges`, {
    challenge: {
      id: 'memory-week',
      name: 'Неделя проверки памяти',
      description: 'Отметка реального прохождения для временного замера.',
      condition: { game: { minHours: 0 } },
      reward: { coins: ruleset.weeklyChallenge.defaultRewardCoins },
      check: 'manual',
    },
    startsAt: completed.completedAt,
  });
  const challenge = list(await read(admin, `${base}/weekly-challenges`), 'weekly challenges').find(
    (item) => !oldChallenges.has(item.id),
  );
  if (!challenge) throw new Error('Memory workload: the new weekly challenge was not projected.');
  await write(admin, `${adminBase}/weekly-challenges/${challenge.id}/mark`, {
    playerId: completed.playerId,
    runId: completed.runId,
  });
  const reads = await assertStage5MemoryReads({ call: players[0], seasonId });
  if (
    !reads.challenges
      .find((item) => item.id === challenge.id)
      ?.completedBy.some((player) => player.playerId === completed.playerId)
  )
    throw new Error('Memory workload: the marked completion was not projected.');
  return prepareIrlProof(admin, players, seasonId, irlEvent.id);
}
