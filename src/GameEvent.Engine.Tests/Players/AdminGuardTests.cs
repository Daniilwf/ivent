using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Players;

/// <summary>Guards found by the C2 audit and review: bad resource names, ceilings, finish transfers, closed seasons.</summary>
public class AdminGuardTests
{
    private const string Comment = "Исправление по жалобе";

    private static AdjustPlayer Adjust(Scenario s, string comment = Comment, string? cellId = null, int points = 0, int coins = 0, params ResourceDelta[] resources) =>
        new(s.PlayerId("Вася"), comment, cellId, points, coins, [.. resources]);

    public static TheoryData<string, ResourceDelta[]> BadResources() => new()
    {
        { "missing entry", [null!] },
        { "null name", [new ResourceDelta(null!, 1)] },
        { "empty name", [new ResourceDelta("", 1)] },
        { "blank name", [new ResourceDelta("   ", 1)] },
        { "points is a field", [new ResourceDelta("points", 1)] },
        { "coins is a field", [new ResourceDelta("Coins", 1)] },
        { "repeated name", [new ResourceDelta("tickets", 2), new ResourceDelta("tickets", -2)] },
    };

    [Theory]
    [MemberData(nameof(BadResources))]
    public void Bad_resource_names_are_rejected_not_crashing(string what, ResourceDelta[] resources)
    {
        _ = what;
        var s = Scenario.New().WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, resources: resources)), RejectionCodes.InvalidResource);
    }

    [Theory]
    [InlineData(1_000_001, 0)]
    [InlineData(0, -1_000_001)]
    [InlineData(int.MaxValue, 0)]
    public void Huge_deltas_are_rejected(int points, int coins)
    {
        var s = Scenario.New().WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, points: points, coins: coins)), RejectionCodes.DeltaTooLarge);
    }

    [Fact]
    public void Huge_resource_delta_is_rejected()
    {
        var s = Scenario.New().WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(Adjust(x, resources: new ResourceDelta("tickets", 2_000_000))), RejectionCodes.DeltaTooLarge);
    }

    [Fact]
    public void Too_long_comment_is_rejected()
    {
        var s = Scenario.New().WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, comment: new string('я', 501), points: 1)), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Transfer_to_the_finish_is_rejected()
    {
        var s = Scenario.New().WithMapLength(10).WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(Adjust(x, cellId: LinearMap.FinishId)), RejectionCodes.TransferToFinish);
    }

    [Fact]
    public void Joining_on_the_finish_is_rejected()
    {
        var s = Scenario.New().WithMapLength(10).WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new AddSeasonPlayer(SequentialIds.Make(0x10000000, 99), SequentialIds.Make(0x40000000, 99), "Петя", LinearMap.FinishId)),
            RejectionCodes.TransferToFinish);
    }

    [Fact]
    public void Huge_starting_balance_is_rejected()
    {
        var s = Scenario.New().WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new AddSeasonPlayer(SequentialIds.Make(0x10000000, 99), SequentialIds.Make(0x40000000, 99), "Петя", Points: 2_000_000)),
            RejectionCodes.DeltaTooLarge);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Inactivity_flag_is_fixed_once_the_season_is_over(SeasonStatus status)
    {
        var s = Scenario.New().WithPlayers("Вася");
        foreach (var next in new[] { SeasonStatus.Closing, SeasonStatus.Finished, SeasonStatus.Archived }.Where(x => x <= status))
        {
            s.Act(new ChangeSeasonStatus(next));
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SetPlayerInactive(x.PlayerId("Вася"), true)), RejectionCodes.SeasonClosed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Season_needs_a_name(string name)
    {
        var s = Scenario.New();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new CreateSeason(SequentialIds.Make(0x30000000, 1), name, x.Ruleset)), RejectionCodes.SeasonInvalidName);
    }

    [Fact]
    public void Season_name_is_limited()
    {
        var s = Scenario.New();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new CreateSeason(SequentialIds.Make(0x30000000, 1), new string('ы', 101), x.Ruleset)), RejectionCodes.SeasonInvalidName);
    }

    [Fact]
    public void Deadline_is_stored_in_utc_and_setting_the_same_again_changes_nothing()
    {
        var s = Scenario.New().WithPlayers("Вася");
        var moscow = new DateTimeOffset(2026, 10, 21, 23, 59, 0, TimeSpan.FromHours(3));

        s.Act(new SetSeasonDeadline(moscow));

        Assert.Equal(TimeSpan.Zero, Assert.Single(s.LastEvents<SeasonDeadlineSet>()).Deadline!.Value.Offset);
        Assert.Equal(moscow, s.State.Deadline);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SetSeasonDeadline(moscow.ToUniversalTime())), RejectionCodes.SeasonNothingToChange);
    }
}
