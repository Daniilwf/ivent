using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Statistics;

public sealed record PlayerDropStatistics(Guid PlayerId, int DroppedRuns);

public sealed record SeasonDropStatistics(
    EquatableArray<PlayerDropStatistics> Players,
    EquatableArray<Guid> MostDropsCandidates);

public static class DropStatistics
{
    /// <summary>Counts the current dropped runs of each roller and returns all tied positive candidates.</summary>
    public static SeasonDropStatistics Build(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var counts = state.Runs.Values.Where(run => run.Status == RunStatus.Dropped)
            .GroupBy(run => run.PlayerId).ToDictionary(group => group.Key, group => group.Count());
        var players = state.Players.Keys.Order()
            .Select(playerId => new PlayerDropStatistics(playerId, counts.GetValueOrDefault(playerId)))
            .ToEquatableArray();

        EquatableArray<Guid> candidates = [];
        if (state.IsCreated && state.Rules.Nominations.Auto.Contains(AutoNomination.MostDrops)
            && players.MaxBy(player => player.DroppedRuns) is { DroppedRuns: > 0 } leader)
        {
            candidates = players.Where(player => player.DroppedRuns == leader.DroppedRuns)
                .Select(player => player.PlayerId)
                .ToEquatableArray();
        }

        return new(players, candidates);
    }
}
