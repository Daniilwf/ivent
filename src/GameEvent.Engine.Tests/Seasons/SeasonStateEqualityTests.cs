using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Seasons;

/// <summary>
/// A season's state compares by content (invariant 4: a replay equals the stored state; the integrity check, D-104):
/// any one of the thirteen compared fields that differs makes two states different. Found by mutation testing (D-282).
/// </summary>
public class SeasonStateEqualityTests
{
    // A started run, so the runs differ when they are cleared
    private static SeasonState Season() => Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася").RollTitle("Вася", "Silent Hill").Start("Вася").State;

    private static PendingManualEffect Effect { get; } =
        new(Guid.Parse("0e000000-0000-0000-0000-000000000001"), Guid.Parse("0a000000-0000-0000-0000-000000000001"), EventKind.Bad, ManualEffectSource.PaidReroll, null);

    public static TheoryData<string, Func<SeasonState, SeasonState>> OneFieldChanged() => new()
    {
        { "season id", s => s with { SeasonId = Guid.Parse("0d000000-0000-0000-0000-000000000001") } },
        { "name", s => s with { Name = s.Name + "!" } },
        { "status", s => s with { Status = SeasonStatus.Closing } },
        { "deadline", s => s with { Deadline = s.Deadline?.AddDays(1) ?? DateTimeOffset.UnixEpoch } },
        { "ruleset version", s => s with { RulesetVersion = s.RulesetVersion + 1 } },
        { "finishes so far", s => s with { FinishesSoFar = s.FinishesSoFar + 1 } },
        { "points changes", s => s with { PointsChanges = s.PointsChanges + 1 } },
        { "players", s => s with { Players = s.Players.Clear() } },
        { "ruleset", s => s with { Ruleset = null } },
        { "map", s => s with { Map = new MapGraph([], []) } },
        { "runs", s => s with { Runs = s.Runs.Clear() } },
        { "manual effects", s => s with { ManualEffects = s.ManualEffects.Add(Effect.EffectId, Effect) } },
        { "result", s => s with { Result = new EquatableArray<GameEvent.Engine.Ranking.LeaderboardRow>([]) } },
    };

    [Theory]
    [MemberData(nameof(OneFieldChanged))]
    public void One_different_field_makes_another_state(string field, Func<SeasonState, SeasonState> change)
    {
        var season = Season();
        Assert.NotEmpty(season.Runs);
        Assert.Null(season.Result);

        Assert.False(season.Equals(change(season)), $"{field} is not compared");
        Assert.True(season.Equals(season with { }));
        Assert.False(season.Equals(null));
    }

    [Fact]
    public void The_empty_season_has_no_name()
    {
        Assert.Equal("", SeasonState.Empty.Name);
        Assert.False(SeasonState.Empty.IsCreated);
    }
}
