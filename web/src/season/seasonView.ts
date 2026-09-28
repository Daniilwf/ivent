import type { Schemas } from '../api/client';
import type { LeaderRow } from '../board/Leaderboard';
import type { Board, Player } from '../board/types';

type Season = Schemas['SeasonView'];

export type SeasonPicture = {
  board: Board;
  players: Player[];
  rows: LeaderRow[];
  /** The board's number of each of the server's cells */
  cellNumber: Map<string, number>;
};

/** What the season screen draws from the server's view: the board of its cells, the players on it, the leaderboard */
export function seasonPicture(
  season: Season | null,
  { board, cellNumber }: Pick<SeasonPicture, 'board' | 'cellNumber'>,
): SeasonPicture {
  if (!season) return { board, players: [], rows: [], cellNumber };

  const rowOf = new Map(season.leaderboard.map((r) => [r.playerId, r]));
  // A player's token colour comes from the server, the same in every view of the season (D-202)
  const players: Player[] = season.players.map((p) => {
    const row = rowOf.get(p.id);
    return {
      id: p.id,
      name: p.name,
      token: p.token,
      avatar: p.avatar?.thumbnailUrl,
      // A cell the chain does not know (it should not happen) draws no token rather than a wrong one at the start
      cell: cellNumber.get(p.cellId) ?? 0,
      points: p.points,
      me: p.id === season.me?.playerId,
      first: row?.isFirst === true,
    };
  });
  const byId = new Map(players.map((p) => [p.id, p]));
  const rows: LeaderRow[] = season.leaderboard.flatMap((r) => {
    const player = byId.get(r.playerId);
    return player
      ? [
          {
            player,
            place: r.place,
            points: r.points,
            cellsToFinish: r.cellsToFinish ?? null,
            isFirst: r.isFirst,
            provisional: r.provisional,
          },
        ]
      : [];
  });
  return { board, players, rows, cellNumber };
}
