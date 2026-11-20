const guid = (value) =>
  typeof value === 'string' &&
  /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value) &&
  value !== '00000000-0000-0000-0000-000000000000';
const text = (value) => typeof value === 'string' && value.trim().length > 0;
const positiveInteger = (value) => Number.isInteger(value) && value > 0;
const dated = (value) => text(value) && Number.isFinite(Date.parse(value));
const rows = (value) => (Array.isArray(value) ? value : []);

async function read(call, path) {
  const response = await call(path);
  if (!response.ok) throw new Error(`Memory workload: GET ${path}: HTTP ${response.status}`);
  return response;
}

async function readJson(call, path) {
  const response = await read(call, path);
  try {
    return await response.json();
  } catch (cause) {
    throw new Error(`Memory workload: GET ${path} did not return JSON.`, { cause });
  }
}

async function assertThumbnail(call, path) {
  const response = await read(call, path);
  const bytes = Buffer.from(await response.arrayBuffer());
  if (
    response.headers.get('content-type') !== 'image/webp' ||
    bytes.length < 20 ||
    bytes.toString('ascii', 0, 4) !== 'RIFF' ||
    bytes.toString('ascii', 8, 12) !== 'WEBP'
  ) {
    throw new Error(`Memory workload: GET ${path} did not return a WebP thumbnail.`);
  }
}

const hasReaction = (value) =>
  rows(value).some((reaction) => text(reaction?.emoji) && positiveInteger(reaction.count));

async function discoverDiscussion(call, season) {
  const feed = await readJson(call, `${season}/feed?limit=100`);
  const discussionIds = new Set(rows(feed?.commentTargets));
  return rows(feed?.entries).find(
    (row) =>
      guid(row?.commandId) &&
      row.undone === false &&
      discussionIds.has(row.commandId) &&
      hasReaction(feed.reactions?.[row.commandId]),
  )?.commandId;
}

// Discover the demo data before the stage 5 setup adds new feed entries. Recheck those same IDs after the load.
export async function assertStage6MemoryReads({ call, seasonId, workload }) {
  if (!guid(seasonId)) throw new Error('Memory workload: stage 6 requires a valid season ID.');
  const season = `/api/seasons/${seasonId}`;
  const gallery = `${season}/gallery`;
  const statistics = `${season}/my-statistics`;
  const hall = '/api/hall-of-fame';
  const records = `${hall}/records`;

  const galleryView = await readJson(call, gallery);
  const moment = rows(galleryView?.items).find(
    (row) =>
      guid(row?.id) &&
      (!workload || row.id === workload.itemId) &&
      guid(row.playerId) &&
      text(row.playerName) &&
      text(row.caption) &&
      dated(row.createdAt) &&
      row.imageUrl === `${gallery}/${row.id}/image` &&
      row.thumbnailUrl === `${gallery}/${row.id}/thumbnail`,
  );
  if (!moment)
    throw new Error('Memory workload: no visible gallery moment with valid image links.');
  await assertThumbnail(call, moment.thumbnailUrl);

  const targetId = workload ? workload.targetId : await discoverDiscussion(call, season);
  if (!guid(targetId)) throw new Error('Memory workload: no discussed feed entry with a reaction.');
  const comments = `${season}/feed/${targetId}/comments`;
  const reactions = `${season}/feed/reactions?commandIds=${targetId}`;
  const thread = await readJson(call, comments);
  if (
    !rows(thread?.comments).some(
      (row) => guid(row?.id) && text(row.author) && text(row.text) && dated(row.createdAt),
    )
  ) {
    throw new Error('Memory workload: the feed discussion has no visible comment.');
  }
  if (!hasReaction((await readJson(call, reactions))?.[targetId])) {
    throw new Error('Memory workload: the discussed entry has no positive feed reaction count.');
  }

  const recap = await readJson(call, statistics);
  if (
    recap?.seasonId !== seasonId ||
    !text(recap.seasonName) ||
    !positiveInteger(recap.completedRuns)
  ) {
    throw new Error('Memory workload: no private recap with completed runs in the current season.');
  }

  const winners = rows(await readJson(call, hall)).filter(
    (row) =>
      guid(row?.seasonId) &&
      row.seasonId !== seasonId &&
      text(row.seasonName) &&
      rows(row.winners).some(
        (winner) => guid(winner?.playerId) && text(winner.name) && Number.isInteger(winner.points),
      ),
  );
  if (winners.length === 0) throw new Error('Memory workload: no archived season with winners.');
  const archivedIds = new Set(winners.map((winner) => winner.seasonId));
  const recordView = await readJson(call, records);
  if (
    !rows(recordView?.longestGames).some(
      (row) =>
        archivedIds.has(row?.seasonId) &&
        guid(row.gameId) &&
        guid(row.runId) &&
        text(row.title) &&
        Number.isFinite(row.hours) &&
        row.hours > 0,
    )
  ) {
    throw new Error('Memory workload: no longest game from an archived season with winners.');
  }

  return {
    itemId: moment.id,
    targetId,
    pages: [gallery, moment.thumbnailUrl, comments, reactions, statistics, hall, records],
  };
}
