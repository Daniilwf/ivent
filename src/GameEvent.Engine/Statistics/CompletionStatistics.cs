using GameEvent.Engine.Kernel;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Statistics;

public sealed record PlayerCompletionStatistics(Guid PlayerId, int CompletedRuns);

public sealed record SeasonCompletionStatistics(
    EquatableArray<PlayerCompletionStatistics> Players,
    EquatableArray<Guid> MostRunsCandidates);

public static class CompletionStatistics
{
    /// <summary>Builds personal completion counts and all tied MostRuns candidates, ordered by player id.</summary>
    public static SeasonCompletionStatistics Build(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var players = Leaderboard.Entries(state)
            .OrderBy(entry => entry.PlayerId)
            .Select(entry => new PlayerCompletionStatistics(entry.PlayerId, entry.CompletedRuns))
            .ToEquatableArray();

        EquatableArray<Guid> candidates = [];
        if (state.IsCreated && state.Rules.Nominations.Auto.Contains(AutoNomination.MostRuns)
            && players.MaxBy(player => player.CompletedRuns) is { CompletedRuns: > 0 } leader)
        {
            candidates = players.Where(player => player.CompletedRuns == leader.CompletedRuns)
                .Select(player => player.PlayerId)
                .ToEquatableArray();
        }

        return new(players, candidates);
    }
}
