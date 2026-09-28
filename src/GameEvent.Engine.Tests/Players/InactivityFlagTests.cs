using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Players;

/// <summary>
/// SE5 (the flag): only the admin marks a player inactive or back, and each change is an event.
/// The «no actions for N days» hint is a read model of the admin area (E2), not an engine rule.
/// </summary>
public class InactivityFlagTests
{
    private static Scenario RunningSeason() =>
        Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася", "Петя");

    [Fact]
    public void Admin_marks_a_player_inactive()
    {
        var s = RunningSeason();

        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PlayerInactivitySet(s.PlayerId("Вася"), true)], s.Last.Events);
        Assert.True(s.Player("Вася").IsInactive);
        Assert.False(s.Player("Петя").IsInactive);
    }

    [Fact]
    public void Admin_brings_an_inactive_player_back()
    {
        var s = RunningSeason();
        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true));
        ScenarioAssert.Accepted(s);

        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), false));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PlayerInactivitySet(s.PlayerId("Вася"), false)], s.Last.Events);
        Assert.False(s.Player("Вася").IsInactive);
    }

    [Fact]
    public void Marking_an_inactive_player_inactive_again_is_rejected()
    {
        var s = RunningSeason();
        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SetPlayerInactive(x.PlayerId("Вася"), true)), RejectionCodes.NothingToChange);
    }

    [Fact]
    public void Bringing_back_an_active_player_is_rejected()
    {
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SetPlayerInactive(x.PlayerId("Вася"), false)), RejectionCodes.NothingToChange);
    }

    [Fact]
    public void Unknown_player_is_rejected()
    {
        var s = RunningSeason();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SetPlayerInactive(SequentialIds.Make(0x10000000, 0x99), true)), RejectionCodes.PlayerUnknown);
    }

    [Fact]
    public void Flag_changes_nothing_else_about_the_player()
    {
        // A long run is not a reason for the flag, and the flag does not end the run
        var s = RunningSeason().Roll("Вася").Start("Вася");
        var before = s.Player("Вася");

        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true));

        ScenarioAssert.Accepted(s);
        Assert.Equal(before with { IsInactive = true }, s.Player("Вася"));
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
    }

    [Fact]
    public void Player_can_be_marked_inactive_in_a_draft_season()
    {
        var s = Scenario.New().AsDraft().WithPlayers("Вася");

        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true));

        ScenarioAssert.Accepted(s);
        Assert.True(s.Player("Вася").IsInactive);
    }

    [Fact]
    public void Flag_survives_replay()
    {
        var s = RunningSeason();
        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true));
        s.Act(new SetPlayerInactive(s.PlayerId("Петя"), true));
        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), false));
        ScenarioAssert.Accepted(s);

        var replayed = SeasonEngine.Replay(s.Log.Select(EventCodec.Encode).Select(EventCodec.Decode));

        Assert.Equal(s.State, replayed);
        Assert.False(replayed.Players[s.PlayerId("Вася")].IsInactive);
        Assert.True(replayed.Players[s.PlayerId("Петя")].IsInactive);
    }
}
