using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// The admin's hours and difficulty corrections around the finish (RR8; D-97, D-99, Q-3): a correction of a finisher's
/// run up to the finish goes through the surplus — an increase adds to it, a reduction within it takes from it, a
/// reduction beyond it revokes the finish, moves the token back by the rest and recalculates the bonuses (Q-4); a
/// correction of a run after the finish changes points and coins only; a frozen first gets nothing
/// (<see cref="FreeModeTests"/>). A correction never brings a player who has not finished to the finish: the token stops
/// one cell before it, only a run's completion finishes.
/// </summary>
public class FinisherCorrectionTests
{
    // ---- Players who have not finished ----

    [Fact]
    public void Forward_hours_correction_that_would_reach_the_finish_stops_one_cell_before()
    {
        // 1 + 1 → c2; 6 → 12 hours adds 2 + 2: four steps would reach the finish
        var s = New();
        var vasya = s.PlayerId("Вася");
        var (runId, _) = Complete(s, "Вася", [1, 1]);

        s.NextRandom(2, 2).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(vasya, 4, PointsReason.RunCorrection, runId)], s.LastEvents<PointsChanged>());
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(("c2", "c3", MoveReason.RunCorrection), (moved.From, moved.To, moved.Reason));
        Assert.Equal(["c3"], moved.Path);
        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Null(FinishOf(s, "Вася"));
        Assert.Null(FinishLine.First(s.State));
        Assert.Equal(("c3", 6), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Forward_difficulty_change_that_would_reach_the_finish_stops_one_cell_before()
    {
        // 1 + 1 on normal → c2; hard: ⌈1·6/4⌉ + ⌈1·6/4⌉ = 2 + 2 → two more steps would reach the finish
        var s = New();
        var (runId, _) = Complete(s, "Вася", [1, 1]);

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "По пруфу — сложная"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(2, s.LastEvents<PointsChanged>().Single().Delta);
        Assert.Equal("c3", s.LastEvents<PlayerMoved>().Single().To);
        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Equal("c3", s.Player("Вася").CellId);
    }

    [Fact]
    public void Forward_correction_on_the_cell_before_the_finish_writes_no_move()
    {
        // 1 + 2 → c3: there is no cell to enter short of the finish (D-47: no event without a cell)
        var s = New();
        var (runId, _) = Complete(s, "Вася", [1, 2]);

        s.NextRandom(2, 2).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Equal(4, s.LastEvents<PointsChanged>().Single().Delta);
        Assert.Equal("c3", s.Player("Вася").CellId);
    }

    [Fact]
    public void Forward_correction_short_of_the_finish_moves_by_all_its_steps()
    {
        // 1 + 1 → c2, the admin moves Вася to c1; 6 → 9 hours adds one die (1): c1 → c2, not cut
        var s = New();
        var (runId, _) = Complete(s, "Вася", [1, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "c1"));
        ScenarioAssert.Accepted(s);

        s.NextRandom(1).Act(new CorrectRunHours(runId, 9, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(("c1", "c2", 1), (moved.From, moved.To, moved.Steps));
    }

    // ---- Finishers: runs up to the finish (Q-3 surplus) ----

    [Fact]
    public void Increase_of_the_finishing_run_adds_to_the_surplus()
    {
        // Петя's finishing run 6 → 12 hours adds 2 + 2: +4 points, +6 coins, the 4 steps burn (surplus 0 → 4)
        var s = New();
        FinishRun(s, "Вася");
        var petyaRun = FinishRun(s, "Петя");
        var petya = s.PlayerId("Петя");

        s.NextRandom(2, 2).Act(new CorrectRunHours(petyaRun, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(petya, 4, PointsReason.RunCorrection, petyaRun)], s.LastEvents<PointsChanged>());
        Assert.Equal([new CoinsChanged(petya, 6, CoinsReason.RunCorrection, petyaRun)], s.LastEvents<CoinsChanged>());
        Assert.Equal([new FinishSurplusChanged(petya, 4)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal((18, LinearMap.FinishId, 4), (s.Player("Петя").Points, s.Player("Петя").CellId, FinishOf(s, "Петя")!.Surplus));
    }

    [Fact]
    public void Increase_of_an_earlier_run_up_to_the_finish_adds_to_the_surplus()
    {
        // Run A 1 + 1 → c2, run B 1 + 1 → the finish; A normal → hard: 2 + 2, two more steps burn
        var s = New();
        var vasya = s.PlayerId("Вася");
        var (runA, _) = Complete(s, "Вася", [1, 1]);
        Complete(s, "Вася", [1, 1]);

        s.Act(new ChangeRunDifficulty(runA, Difficulty.Hard, "По пруфу — сложная"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new FinishSurplusChanged(vasya, 2)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(2, FinishOf(s, "Вася")!.Surplus);
    }

    [Fact]
    public void Reduction_within_the_surplus_keeps_the_finish_and_the_cell()
    {
        // Петя finishes with 4 + 4 (surplus 4); 6 → 3 hours takes the last die (4): −4 points, −3 coins, surplus 0
        var s = New();
        FinishRun(s, "Вася");
        var (petyaRun, _) = Complete(s, "Петя", [4, 4]);
        var petya = s.PlayerId("Петя");
        var finish = FinishOf(s, "Петя")!;
        Assert.Equal(4, finish.Surplus);

        s.Act(new CorrectRunHours(petyaRun, 3, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(petya, -4, PointsReason.RunCorrection, petyaRun)], s.LastEvents<PointsChanged>());
        Assert.Equal(-3, s.LastEvents<CoinsChanged>().Single().Delta);
        Assert.Equal([new FinishSurplusChanged(petya, -4)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<PlayerFinishRevoked>());
        Assert.Empty(BonusChanges(s));
        Assert.Equal(LinearMap.FinishId, s.Player("Петя").CellId);
        Assert.Equal(finish with { Surplus = 0 }, FinishOf(s, "Петя"));
    }

    [Fact]
    public void Reduction_beyond_the_surplus_revokes_the_finish_moves_back_and_recalculates_bonuses()
    {
        // Вася 1, Петя 2 (+10, surplus 0), Маша 3 (+8). Петя's finishing run 6 → 3 hours takes the die 1: k = 1 > 0
        var s = New();
        FinishRun(s, "Вася");
        var petyaRun = FinishRun(s, "Петя");
        FinishRun(s, "Маша");
        var (petya, masha) = (s.PlayerId("Петя"), s.PlayerId("Маша"));

        s.Act(new CorrectRunHours(petyaRun, 3, "Часы по HLTB"));

        // The finish goes, the token steps back k − surplus = 1 cell, his bonus goes; Маша is second now: 8 → 10
        ScenarioAssert.Accepted(s);
        Assert.Equal(petya, Assert.Single(s.LastEvents<PlayerFinishRevoked>()).PlayerId);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, "c3", MoveReason.RunCorrection, (Guid?)petyaRun), (moved.From, moved.To, moved.Reason, moved.RunId));
        Assert.Equal(
            [(masha, 2, PointsReason.FinishBonus), (petya, -10, PointsReason.FinishBonusRevoked)],
            BonusChanges(s).OrderBy(x => x.Delta).Reverse());
        Assert.Null(FinishOf(s, "Петя"));
        Assert.Equal(3, s.Player("Петя").Points);
        Assert.Equal((3, 10, 14), (FinishOf(s, "Маша")!.Order, FinishOf(s, "Маша")!.Bonus, s.Player("Маша").Points));
    }

    [Fact]
    public void Provisional_first_difficulty_change_down_within_the_surplus_keeps_the_first_place()
    {
        // 4 + 4 normal (surplus 4) → easy ⌈4·2/4⌉ × 2 = 2 + 2: −4 points, surplus 0; the finish and the first place stay
        var s = New();
        var (runId, _) = Complete(s, "Вася", [4, 4]);

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Easy, "По пруфу — лёгкая"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(-4, s.LastEvents<PointsChanged>().Single().Delta);
        Assert.Equal([new FinishSurplusChanged(s.PlayerId("Вася"), -4)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
        Assert.Equal(LinearMap.FinishId, s.Player("Вася").CellId);
    }

    // ---- Finishers: runs after the finish ----

    [Fact]
    public void Correction_of_a_run_after_the_finish_changes_points_only()
    {
        // Петя's run after his finish 2 + 2, 6 → 3 hours: −2 points, −3 coins; no surplus change, no move
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        var (later, _) = Complete(s, "Петя", [2, 2]);
        var finish = FinishOf(s, "Петя");

        s.Act(new CorrectRunHours(later, 3, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(-2, s.LastEvents<PointsChanged>().Single().Delta);
        Assert.Equal(-3, s.LastEvents<CoinsChanged>().Single().Delta);
        Assert.Empty(s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(finish, FinishOf(s, "Петя"));
    }

    [Fact]
    public void Difficulty_change_of_a_run_after_the_finish_changes_points_only()
    {
        // 3 + 1 normal → hard 5 + 2: +3 points, no surplus change
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        var (later, _) = Complete(s, "Петя", [3, 1]);

        s.Act(new ChangeRunDifficulty(later, Difficulty.Hard, "По пруфу — сложная"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, s.LastEvents<PointsChanged>().Single().Delta);
        Assert.Empty(s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(LinearMap.FinishId, s.Player("Петя").CellId);
    }
}
