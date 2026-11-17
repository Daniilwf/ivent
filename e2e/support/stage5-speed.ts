import { readFileSync } from 'node:fs';
import type { Api, Schemas } from './api.ts';

// Only the E2E speed player's demo season: production DemoSeed and its other objects stay as they are.
const trialId = 'speed-trial';
const kitId = 'speed-kit';
const trialDeck = 'speed-trial-deck';
const irlDeck = 'speed-irl-deck';

function ready(me: Schemas['MyTurnView'] | null): boolean {
  return (
    me?.eventsEnabled === true &&
    me.phase === 'idle' &&
    me.waitingConditions?.some(
      (condition) => condition.objectId === trialId && condition.difficultyAtLeast === 'hard',
    ) === true &&
    me.waitingEvents?.some(
      (event) =>
        event.objectId === trialId &&
        event.waitingForRoll === false &&
        event.waitingForThrow === true,
    ) === true &&
    me.manualEffects.some(
      (effect) => effect.objectId === 'karaoke' && effect.irl && !effect.irlProof,
    ) &&
    me.lastEvent?.objectId === 'karaoke'
  );
}

function jsonObject(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value))
    throw new Error('The demo content must contain JSON objects.');
  return value as Record<string, unknown>;
}

/** Keep every existing definition and its order; replace only definitions explicitly named by this fixture. */
function mergeById(existing: unknown, additions: readonly unknown[]): unknown[] {
  if (!Array.isArray(existing))
    throw new Error('The demo content must contain objects and wheels arrays.');
  const merged = new Map<string, unknown>();
  for (const entry of [...(existing as unknown[]), ...additions]) {
    const id = jsonObject(entry)['id'];
    if (typeof id !== 'string') throw new Error('A demo content definition has no string id.');
    merged.set(id, entry);
  }
  return [...merged.values()];
}

/** Queue real effects before any E2E scenario advances the shared clock beyond the demo deadline. */
export async function prepareStage5Speed(admin: Api, player: Api, seasonId: string): Promise<void> {
  const path = `/api/seasons/${seasonId}`;
  const adminPath = `/api/admin/seasons/${seasonId}`;
  const before = await player.get<Schemas['SeasonView']>(path);
  // A local E2E site may be reused after its clock moved: the prepared state needs no further writes.
  if (ready(before.me)) return;
  if (!before.me)
    throw new Error('The E2E speed player must join the demo season before its stage 5 setup.');
  if (before.status !== 'active')
    throw new Error(
      'Stage 5 speed setup needs a fresh, active E2E demo season before the clock is advanced.',
    );
  const economy = await player.get<Schemas['EconomyView']>(`${path}/economy`);
  if (before.deadline && Date.parse(economy.serverNow) >= Date.parse(before.deadline))
    throw new Error(
      'Stage 5 speed setup cannot run after the demo deadline; start a fresh E2E site.',
    );

  const rules = await admin.get<Schemas['RulesView']>(`${path}/rules`);
  if (!rules.ruleset.features.items)
    throw new Error('Stage 5 speed setup expects the demo season to have items enabled.');
  if (!rules.ruleset.features.events)
    await admin.put(`${adminPath}/rules`, {
      expectedVersion: rules.version,
      ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, events: true } },
    });

  const content = await admin.get<Schemas['AdminContentView']>(`${adminPath}/content`);
  const pack = jsonObject(content.pack);
  const karaoke: unknown = JSON.parse(
    readFileSync(new URL('../../content/events/karaoke.json', import.meta.url), 'utf8'),
  );
  const trial = {
    id: trialId,
    kind: 'event',
    name: 'Испытание скорости',
    description:
      'Следующую игру нужно пройти на сложной сложности или выше. В следующем броске на один кубик больше.',
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
    name: 'Набор для замера скорости',
    description: 'Разыграй испытание и IRL-ивент для проверки экрана сезона.',
    rarity: 'common',
    price: 1,
    window: 'anytime',
    hostile: false,
    stackable: false,
    effect: {
      target: { selector: 'self' },
      actions: [
        { type: 'drawEvent', deck: trialDeck },
        { type: 'drawEvent', deck: irlDeck },
      ],
    },
  };
  const decks = [
    {
      id: trialDeck,
      name: 'Испытание для замера',
      deck: 'bad',
      entries: [{ objectId: trialId, weight: 1 }],
    },
    {
      id: irlDeck,
      name: 'IRL-ивент для замера',
      deck: 'bad',
      entries: [{ objectId: 'karaoke', weight: 1 }],
    },
  ];
  await admin.post(`${adminPath}/content/publish`, {
    expectedVersion: content.version,
    pack: {
      ...pack,
      objects: mergeById(pack['objects'], [karaoke, trial, kit]),
      wheels: mergeById(pack['wheels'], decks),
    },
    comment: 'Ивенты для замера скорости E2E',
  });

  // Resume a setup interrupted after the grant, without granting a second copy.
  let instance = economy.items?.find((item) => item.objectId === kitId);
  if (!instance) {
    await admin.post(`${adminPath}/players/${before.me.playerId}/inventory`, {
      giveObjectId: kitId,
      comment: 'Подготовка замера скорости E2E',
    });
    instance = (await player.get<Schemas['EconomyView']>(`${path}/economy`)).items?.find(
      (item) => item.objectId === kitId,
    );
  }
  if (!instance) throw new Error('The stage 5 speed kit was not granted to the E2E speed player.');
  await player.post(`${path}/items/${instance.instanceId}/use`);
  if (!ready((await player.get<Schemas['SeasonView']>(path)).me))
    throw new Error(
      'Stage 5 speed setup did not create its condition, pending throw modifier, IRL task and last draw.',
    );
}
