using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Proofs.ProofSetup;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// Cells a run really moved and whether it brought the player to the finish (W5, W7, SE6; SPEC «Реджект: снимаются
/// очки и клетки, полученные за это прохождение»; D-47, D-98 and its «Уточнения после ревью» (1), (2)).
/// <see cref="RunState.Moved"/> counts the cells entered, not the steps rolled: steps burned at the finish give no
/// cells. The reject moves back exactly those cells; an hours correction down moves back only the cells beyond the new
/// dice sum. <see cref="RunState.ReachedFinish"/> follows the run's latest move, so a correction that leaves the finish
/// clears it. Since C9a (Q-3, D-99) a finisher's runs up to the finish go through the surplus — the steps burned at the
/// finish: a reduction within it moves nothing, beyond it the finish is revoked and the token goes back by the rest; a
/// correction never brings a player who has not finished to the finish. The review queue puts the runs of standing
/// finishers up to their finish on top, by completion time.
/// The map is 8 steps; every game is 9 hours, so three d4 by hours (hoursPerDie 3). Вася's run A (2 + 2 + 2) brings
/// him to c6; run B (1 + 4 + 2 = 7) is 2 cells from the finish, so 5 of its steps burn: surplus 5.
/// </summary>
public class MovedCellsTests
{
    private const string Hours = "Часы по HLTB";

    private static (Scenario S, Guid RunA, Guid RunB) Overshoot()
    {
        var s = Scenario.New().WithMapLength(8).WithCategory("Horror");
        foreach (var title in new[] { "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma" })
        {
            s.WithGame(title, 9, "Horror");
        }

        s.WithPlayers("Вася", "Петя", "Маша");
        var runA = CompleteRun(s, "Вася", [2, 2, 2]);
        var runB = CompleteRun(s, "Вася", [1, 4, 2]);
        return (s, runA, runB);
    }

    // ---- Moved and ReachedFinish at completion ----

    [Fact]
    public void Run_that_overshoots_the_finish_moved_only_the_cells_up_to_it()
    {
        var (s, runA, runB) = Overshoot();

        Assert.Equal(LinearMap.FinishId, s.Player("Вася").CellId);
        Assert.Equal(13, s.Player("Вася").Points);
        Assert.Equal((6, false), (s.State.Runs[runA].Moved, s.State.Runs[runA].ReachedFinish));
        Assert.Equal((2, true), (s.State.Runs[runB].Moved, s.State.Runs[runB].ReachedFinish));
        Assert.Equal(5, s.Player("Вася").Finish!.Surplus);

        // Q-3: both runs are up to the finish — on top, by completion time
        Assert.Equal([runA, runB], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Run_that_lands_exactly_on_the_finish_moved_its_whole_sum()
    {
        var s = Scenario.New().WithMapLength(8).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");

        var runId = CompleteRun(s, "Вася", [4, 4]);

        Assert.Equal((8, true), (s.State.Runs[runId].Moved, s.State.Runs[runId].ReachedFinish));
    }

    // ---- The reject takes back the cells really moved ----

    [Fact]
    public void Reject_of_an_overshooting_run_returns_the_player_to_where_that_run_started()
    {
        // Given run B moved c6 → finish (2 cells) of its 7 points; Петя has a run of his own
        var (s, runA, runB) = Overshoot();
        CompleteRun(s, "Петя", [1, 1, 1]);
        var vasya = s.PlayerId("Вася");
        var runABefore = s.State.Runs[runA];
        var petya = s.Player("Петя");

        // When run B is rejected
        Reject(s, runB);

        // Then 2 cells back, to c6 — not 7 steps back to c-1
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(vasya, LinearMap.FinishId, "c6", -2, ["c7", "c6"], MoveReason.ProofRejected, runB),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(new PointsChanged(vasya, -7, PointsReason.ProofRejected, runB), Assert.Single(s.LastEvents<PointsChanged>()));

        // And run A's cells and points stay; Петя is untouched
        Assert.Equal((6, "c6"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Equal(runABefore, s.State.Runs[runA]);
        Assert.Equal(petya, s.Player("Петя"));
    }

    [Fact]
    public void Reject_after_a_backward_correction_takes_back_only_the_cells_left()
    {
        // Run B: 2 cells, then 9 → 3 hours leaves one die (1): back 1 to c7; the reject takes back the remaining 1
        var (s, _, runB) = Overshoot();
        s.Act(new CorrectRunHours(runB, 3, Hours));
        ScenarioAssert.Accepted(s);
        Assert.Equal("c7", s.Player("Вася").CellId);

        Reject(s, runB);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(s.PlayerId("Вася"), "c7", "c6", -1, ["c6"], MoveReason.ProofRejected, runB),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal((6, "c6"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    // ---- An hours correction down takes back only the cells beyond the new sum (D-98 (1)) ----

    [Fact]
    public void Correction_down_to_a_sum_still_covering_the_moved_cells_moves_nothing()
    {
        // 9 → 6 hours takes off the last die (2): the new sum 5 still covers the 2 cells moved
        var (s, _, runB) = Overshoot();
        var vasya = s.PlayerId("Вася");

        s.Act(new CorrectRunHours(runB, 6, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(vasya, -2, PointsReason.RunCorrection, runB), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal([new FinishSurplusChanged(vasya, -2)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerFinishRevoked>());
        Assert.Equal((11, LinearMap.FinishId, 3), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Finish!.Surplus));
        Assert.Equal((2, true), (s.State.Runs[runB].Moved, s.State.Runs[runB].ReachedFinish));
    }

    [Fact]
    public void Correction_down_beyond_the_surplus_revokes_the_finish_and_moves_back_the_rest()
    {
        // 9 → 3 hours leaves one die (1): k = 6 > surplus 5 — the finish goes, back 6 − 5 = 1 cell, not 6
        var (s, _, runB) = Overshoot();
        var vasya = s.PlayerId("Вася");

        s.Act(new CorrectRunHours(runB, 3, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(vasya, -6, PointsReason.RunCorrection, runB), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(vasya, Assert.Single(s.LastEvents<PlayerFinishRevoked>()).PlayerId);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((vasya, LinearMap.FinishId, "c7", MoveReason.RunCorrection, (Guid?)runB), (moved.PlayerId, moved.From, moved.To, moved.Reason, moved.RunId));
        Assert.Equal(["c7"], moved.Path);
        Assert.Equal((7, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Null(s.Player("Вася").Finish);
        Assert.Equal(1, s.State.Runs[runB].Moved);
    }

    [Fact]
    public void Two_corrections_down_add_up_to_the_same_cells()
    {
        // 9 → 6 (k = 2 within the surplus 5: surplus 3) and then 6 → 3 (k = 4 > 3: back 1): the same place as 9 → 3
        var (s, _, runB) = Overshoot();
        var vasya = s.PlayerId("Вася");
        s.Act(new CorrectRunHours(runB, 6, Hours));
        ScenarioAssert.Accepted(s);
        Assert.Equal([new FinishSurplusChanged(vasya, -2)], s.LastEvents<FinishSurplusChanged>());

        s.Act(new CorrectRunHours(runB, 3, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<PlayerFinishRevoked>());
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, "c7", MoveReason.RunCorrection), (moved.From, moved.To, moved.Reason));
        Assert.Equal(["c7"], moved.Path);
        Assert.Equal((7, "c7", 1), (s.Player("Вася").Points, s.Player("Вася").CellId, s.State.Runs[runB].Moved));
    }

    [Fact]
    public void Correction_up_on_the_finish_gives_points_but_no_cells()
    {
        // 9 → 12 hours adds a die 3: the player is on the finish, the steps burn (D-47)
        var (s, _, runB) = Overshoot();
        s.NextRandom(3);

        s.Act(new CorrectRunHours(runB, 12, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal([new FinishSurplusChanged(s.PlayerId("Вася"), 3)], s.LastEvents<FinishSurplusChanged>());
        Assert.Equal((16, LinearMap.FinishId, 8), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Finish!.Surplus));
        Assert.Equal((2, true), (s.State.Runs[runB].Moved, s.State.Runs[runB].ReachedFinish));
    }

    // ---- ReachedFinish follows the latest move (D-98 (2)) ----

    [Fact]
    public void Correction_that_revokes_the_finish_clears_reached_finish_and_the_runs_leave_the_top()
    {
        // Петя completes P first; Вася's A and B are up to his finish, so they are on top. 9 → 3 hours revokes the finish:
        // B's latest move now ends on c7 and both of Вася's runs fall back to completion time, behind P
        var s = Scenario.New().WithMapLength(8).WithCategory("Horror");
        foreach (var title in new[] { "Silent Hill", "Alan Wake", "Dead Space", "Outlast" })
        {
            s.WithGame(title, 9, "Horror");
        }

        s.WithPlayers("Вася", "Петя");
        var petyaRun = CompleteRun(s, "Петя", [1, 1, 1]);
        var runA = CompleteRun(s, "Вася", [2, 2, 2]);
        var runB = CompleteRun(s, "Вася", [1, 4, 2]);
        Assert.Equal([runA, runB, petyaRun], ProofReviewOrder.Order(s.State));

        s.Act(new CorrectRunHours(runB, 3, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Null(s.Player("Вася").Finish);
        Assert.False(s.State.Runs[runB].ReachedFinish);
        Assert.Equal([petyaRun, runA, runB], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Correction_up_after_a_revoke_does_not_bring_the_player_back_to_the_finish()
    {
        // Back to c7 by 9 → 3 hours (the finish revoked), then 3 → 6 adds a die 3: the finish is one cell ahead, but a
        // correction never finishes (RR8) — the token stays on c7, the points come
        var (s, runA, runB) = Overshoot();
        s.Act(new CorrectRunHours(runB, 3, Hours));
        ScenarioAssert.Accepted(s);
        s.NextRandom(3);

        s.Act(new CorrectRunHours(runB, 6, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Equal((10, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Null(s.Player("Вася").Finish);
        Assert.Equal((1, false), (s.State.Runs[runB].Moved, s.State.Runs[runB].ReachedFinish));
        Assert.Equal([runA, runB], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Correction_of_an_earlier_run_within_the_surplus_keeps_the_player_on_the_finish()
    {
        // Run A 9 → 3 hours leaves one die (2): k = 4 within the surplus 5 — no move, surplus 1; B's flag stays
        var (s, runA, runB) = Overshoot();

        s.Act(new CorrectRunHours(runA, 3, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new FinishSurplusChanged(s.PlayerId("Вася"), -4)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, 1), (s.Player("Вася").CellId, s.Player("Вася").Finish!.Surplus));
        Assert.True(s.State.Runs[runB].ReachedFinish);
        Assert.False(s.State.Runs[runA].ReachedFinish);
        Assert.Equal(6, s.State.Runs[runA].Moved);
    }

    [Fact]
    public void Second_reduction_beyond_the_remaining_surplus_moves_back_the_rest()
    {
        // Run A 9 → 3 hours takes 4 of the surplus 5 (1 left); then B easy — ⌈1·2/4⌉ + ⌈4·2/4⌉ + ⌈2·2/4⌉ = 1 + 2 + 1 = 4,
        // k = 3 > 1: the finish goes, back 2 cells to c6
        var (s, runA, runB) = Overshoot();
        s.Act(new CorrectRunHours(runA, 3, Hours));
        ScenarioAssert.Accepted(s);
        Assert.Equal(1, s.Player("Вася").Finish!.Surplus);

        s.Act(new ChangeRunDifficulty(runB, Difficulty.Easy, "По пруфу — лёгкая"));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<PlayerFinishRevoked>());
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, "c6", MoveReason.RunCorrection, (Guid?)runB), (moved.From, moved.To, moved.Reason, moved.RunId));
        Assert.Equal(["c7", "c6"], moved.Path);
        Assert.False(s.State.Runs[runB].ReachedFinish);
    }

    // ---- The log ----

    [Fact]
    public void Replaying_the_log_gives_the_same_moved_cells_and_finish_flags()
    {
        var (s, runA, runB) = Overshoot();
        s.Act(new CorrectRunHours(runB, 3, Hours));
        ScenarioAssert.Accepted(s);
        Reject(s, runA);
        ScenarioAssert.Accepted(s);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}
