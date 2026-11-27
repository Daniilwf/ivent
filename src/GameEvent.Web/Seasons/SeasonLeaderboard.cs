using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>The same standing for the season screen and the player's recap.</summary>
internal static class SeasonLeaderboard
{
    internal static async Task<EquatableArray<LeaderboardRow>> ReadAsync(
        GameEventDbContext db, Guid seasonId, RankingRules rules, MapGraph map, IEnumerable<RankingEntry> entries, CancellationToken ct)
    {
        var result = await db.SeasonResults.AsNoTracking().Where(x => x.SeasonId == seasonId).OrderBy(x => x.Row).ToListAsync(ct);
        if (result.Count > 0)
        {
            return [.. result.Select(x => new LeaderboardRow(x.PlayerId, x.Place, x.Points, x.CellsToFinish, x.IsFirst, x.Provisional))];
        }

        var completed = await db.Runs.AsNoTracking()
            .Where(r => r.SeasonId == seasonId && r.Status == RunStatus.Completed)
            .GroupBy(r => r.PlayerId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // A split coop completion counts for both players (D-716).
        foreach (var partnered in await CoopPartnerRuns.LoadAsync(db, [seasonId], null, ct))
        {
            if (partnered.Status == RunStatus.Completed)
            {
                completed[partnered.PartnerId] = completed.GetValueOrDefault(partnered.PartnerId) + 1;
            }
        }

        return Leaderboard.Rank(
            entries.Select(p => p with { CompletedRuns = completed.GetValueOrDefault(p.PlayerId) }),
            rules,
            map);
    }
}
