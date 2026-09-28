using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Ranking;

/// <summary>
/// What ranking needs to know of a player (D-100): points, the cell, the finish order (null — no standing finish),
/// whether frozen, the completed runs that count (completed and not rejected) and <see cref="PointsTick"/> — the
/// number of the season's points change that set the current points (0 — never changed).
/// </summary>
public sealed record RankingEntry(
    Guid PlayerId,
    int Points,
    string CellId,
    int? FinishOrder,
    bool Frozen,
    int CompletedRuns,
    long PointsTick);

/// <summary>
/// A leaderboard row (SPEC «Первое место», «Остальные места», «Тайбрейк», «Лидерборд»; D-100). <see cref="Place"/>
/// starts at 1; tied players share it and the next place skips (1, 2, 2, 4). <see cref="IsFirst"/> — the first
/// finisher, on top whatever the points; <see cref="Provisional"/> — first but not frozen yet. <see cref="CellsToFinish"/>
/// — the fewest forward steps from the player's cell to a finish cell (0 on the finish, null when unreachable).
/// </summary>
public sealed record LeaderboardRow(Guid PlayerId, int Place, int Points, int? CellsToFinish, bool IsFirst, bool Provisional);

/// <summary>The season's places (D-100): the first finisher first, everyone else by points, then the tiebreakers in order.</summary>
public static class Leaderboard
{
    /// <summary>The leaderboard of <paramref name="state"/>, in place order, ties by player id.</summary>
    public static EquatableArray<LeaderboardRow> Build(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Rank(Entries(state), state.Rules.Ranking, state.Map);
    }

    /// <summary>
    /// Ranks <paramref name="entries"/>: the standing finisher with the lowest order is place 1; the rest by points
    /// (more is better), then by <paramref name="rules"/> tiebreakers in their order: <c>completedRuns</c> (more is
    /// better), <c>earliestFinalScore</c> (lower <see cref="RankingEntry.PointsTick"/> is better). Rows in place order,
    /// ties by player id.
    /// </summary>
    public static EquatableArray<LeaderboardRow> Rank(IEnumerable<RankingEntry> entries, Rulesets.RankingRules rules, MapGraph map)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(map);

        var all = entries.ToList();
        var distances = CellsToFinish(map);
        LeaderboardRow Row(RankingEntry e, int place, bool first) =>
            new(e.PlayerId, place, e.Points, distances.TryGetValue(e.CellId, out var d) ? d : null, first, first && !e.Frozen);

        var rows = new List<LeaderboardRow>();
        var first = all.Where(e => e.FinishOrder is not null).OrderBy(e => e.FinishOrder).FirstOrDefault();
        if (first is not null)
        {
            rows.Add(Row(first, 1, first: true));
        }

        int Compare(RankingEntry a, RankingEntry b)
        {
            var byPoints = b.Points.CompareTo(a.Points);
            if (byPoints != 0)
            {
                return byPoints;
            }

            foreach (var tiebreaker in rules.Tiebreakers)
            {
                var by = tiebreaker switch
                {
                    Rulesets.Tiebreaker.CompletedRuns => b.CompletedRuns.CompareTo(a.CompletedRuns),
                    Rulesets.Tiebreaker.EarliestFinalScore => a.PointsTick.CompareTo(b.PointsTick),
                    _ => throw new InvalidOperationException($"Unknown tiebreaker {tiebreaker}."),
                };
                if (by != 0)
                {
                    return by;
                }
            }

            return 0;
        }

        var rest = all.Where(e => e.PlayerId != first?.PlayerId).ToList();
        rest.Sort((a, b) => Compare(a, b) is var c and not 0 ? c : a.PlayerId.CompareTo(b.PlayerId));
        var start = rows.Count + 1;
        for (var i = 0; i < rest.Count; i++)
        {
            var place = i > 0 && Compare(rest[i - 1], rest[i]) == 0 ? rows[^1].Place : start + i;
            rows.Add(Row(rest[i], place, first: false));
        }

        return [.. rows];
    }

    /// <summary>The ranking entries of <paramref name="state"/>'s players.</summary>
    public static IEnumerable<RankingEntry> Entries(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var completed = state.Runs.Values
            .Where(r => r.Status == RunStatus.Completed)
            .CountBy(r => r.PlayerId)
            .ToDictionary(x => x.Key, x => x.Value);
        return state.Players.Values.Select(p => new RankingEntry(
            p.PlayerId,
            p.Points,
            p.CellId,
            p.Finish?.Order,
            p.Finish?.Frozen ?? false,
            completed.GetValueOrDefault(p.PlayerId),
            p.PointsTick));
    }

    /// <inheritdoc cref="MapDistances.ToFinish"/>
    public static IReadOnlyDictionary<string, int> CellsToFinish(MapGraph map) => MapDistances.ToFinish(map);
}
