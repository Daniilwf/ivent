import type { Schemas } from '../api/client';
import type { LeaderRow } from '../board/Leaderboard';
import { linearBoard } from '../board/linearBoard';
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
export function seasonPicture(season: Season | null): SeasonPicture {
  const { board, cellNumber } = linearBoard(season?.cells ?? []);
  if (!season) return { board, players: [], rows: [], cellNumber };

  const rowOf = new Map(season.leaderboard.map((r) => [r.playerId, r]));
  // A player's token colour follows their place in the season's list, which does not change as the game goes
  const players: Player[] = season.players.map((p, i) => {
    const row = rowOf.get(p.id);
    return {
      id: p.id,
      name: p.name,
      token: i,
      avatar: p.avatar?.thumbnailUrl,
      cell: cellNumber.get(p.cellId) ?? 1,
      points: p.points,
      me: p.id === season.me?.playerId,
      first: row?.isFirst === true,
      inactive: row !== undefined && row.cellsToFinish === null,
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
