using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Seasons;

/// <summary>
/// A season's state compares by content (invariant 4: a replay equals the stored state; the integrity check, D-104):
/// any one field that differs makes two states different. Found by mutation testing (D-282).
/// </summary>
public class SeasonStateEqualityTests
{
    private static SeasonState Season() => Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася").State;

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
    };

    [Theory]
    [MemberData(nameof(OneFieldChanged))]
    public void One_different_field_makes_another_state(string field, Func<SeasonState, SeasonState> change)
    {
        var season = Season();

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
