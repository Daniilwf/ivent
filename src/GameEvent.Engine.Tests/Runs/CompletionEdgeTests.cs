using GameEvent.Engine.Map;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>Completion edge cases fixed by decisions D-44 and D-47 and the «deleted game mid-run» hole.</summary>
public class CompletionEdgeTests
{
    [Fact]
    public void Snapshot_hours_win_over_the_player_estimate()
    {
        // Given a 6-hour game (2 dice by the test ruleset) and a player claiming 30 hours
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");

        s.NextRandom(1, 1).Complete("Вася", Difficulty.Normal, estimatedHours: 30);

        // Then the pool hours from the roll count, not the estimate (D-44)
        Assert.Equal(6m, Assert.Single(s.LastEvents<RunCompleted>()).Hours);
        Assert.Equal(2, Assert.Single(s.LastEvents<CompletionRolled>()).Dice.Count);
    }

    [Fact]
    public void Zero_hours_in_the_pool_count_as_missing_hours()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Broken Entry", 0, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Вася"), Engine.Kernel.RejectionCodes.HoursRequired);

        s.NextRandom(2).Complete("Вася", Difficulty.Normal, estimatedHours: 3, hoursSource: "HLTB");
        Assert.Equal(3m, Assert.Single(s.LastEvents<RunCompleted>()).Hours);
    }

    [Fact]
    public void Completing_while_standing_on_the_finish_adds_points_without_a_move()
    {
        // Given a player already on the finish of a 1-step map
        var s = Scenario.New()
            .WithMapLength(1)
            .WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithGame("Alan Wake", 3, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася").NextRandom(2).Complete("Вася");
        Assert.Equal(LinearMap.FinishId, s.Player("Вася").CellId);

        // When he completes another game
        s.Roll("Вася").Start("Вася").NextRandom(3).Complete("Вася");

        // Then points grow, the token stays and no move is logged (D-47)
        Assert.Equal(new PointsChanged(s.PlayerId("Вася"), 3, PointsReason.CompletionRoll, Assert.Single(s.LastEvents<RunCompleted>()).RunId),
            Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(LinearMap.FinishId, s.Player("Вася").CellId);
        Assert.Equal(5, s.Player("Вася").Points);
    }

    [Fact]
    public void Deleting_the_game_mid_run_keeps_the_run_and_its_snapshot()
    {
        // Given a player playing a game that the admin then deletes from the pool
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася")
            .DeleteGame("Silent Hill");

        // When he completes it
        s.NextRandom(2, 3).Complete("Вася");

        // Then the run completes as rolled (soft delete, snapshot in the run)
        var run = s.State.Runs[Assert.Single(s.LastEvents<RunCompleted>()).RunId];
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(s.GameId("Silent Hill"), run.GameId);
        Assert.Equal(6m, run.Hours);
        Assert.Equal(5, s.Player("Вася").Points);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }
}
