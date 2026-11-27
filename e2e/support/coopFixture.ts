import type { Api, Schemas } from './api.ts';

export const coopGameTitle = 'Portal 2';
export const coopTag = 'e2e-coop';

/** Mark one seeded game before the workers start. The extra tag is not a wheel category. */
export async function prepareCoopGame(admin: Api) {
  const games = await admin.get<Schemas['PoolGameView'][]>(
    `/api/pool?query=${encodeURIComponent(coopGameTitle)}`,
  );
  const game = games.find(({ title }) => title === coopGameTitle);
  if (!game) throw new Error(`The E2E pool has no ${coopGameTitle}.`);
  if (game.isCoop && game.tags.includes(coopTag)) return;

  await admin.put<Schemas['PoolGameView']>(`/api/admin/pool/${game.id}`, {
    title: game.title,
    tags: game.tags.includes(coopTag) ? game.tags : [...game.tags, coopTag],
    hours: game.hours,
    year: game.year,
    steamAppId: game.steamAppId,
    coverFileId: game.cover?.id ?? null,
    note: game.note,
    isCoop: true,
    force: true,
    completionCondition: game.completionCondition,
  });
}
