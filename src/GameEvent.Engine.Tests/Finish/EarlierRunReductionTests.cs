using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Proofs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// A reduction of a finisher's EARLIER run — one up to the finish but not the finishing one (Q-3, D-98, D-99): it can
/// take back only cells the run really moved (<see cref="RunState.Moved"/>), not its dice. A reject takes back all of
/// <c>Moved</c> against the surplus; a correction down takes back <c>max(0, Moved − new dice sum)</c> against it.
/// Setup: a map of 8 steps, 9-hour games (three d4). Вася's run A: 2 + 2 + 1 → c5; the admin corrects A to 12 hours
/// with a die 4: 9 steps asked, but a correction stops one cell before the finish (RR8), so A has moved 7 cells for 9 of
/// dice. Run B: 1 + 1 + 1 from c7 — one step to the finish, 2 burn: surplus 2.
/// </summary>
public class EarlierRunReductionTests
{
    private const string Hours = "Часы по HLTB";

    private static (Scenario S, Guid RunA, Guid RunB) Setup()
    {
        var s = Scenario.New().WithMapLength(8).WithCategory("Horror");
        foreach (var title in new[] { "Silent Hill", "Alan Wake", "Dead Space", "Outlast" })
        {
            s.WithGame(title, 9, "Horror");
        }

        s.WithPlayers("Вася", "Петя");
        var runA = ProofSetup.CompleteRun(s, "Вася", [2, 2, 1]);
        s.NextRandom(4).Act(new CorrectRunHours(runA, 12, Hours));
        ScenarioAssert.Accepted(s);
        Assert.Equal("c7", s.Player("Вася").CellId);
        Assert.Equal((7, 9), (s.State.Runs[runA].Moved, s.State.Runs[runA].Dice.Sum(d => d.Value)));

        var runB = ProofSetup.CompleteRun(s, "Вася", [1, 1, 1]);
        Assert.Equal(2, s.Player("Вася").Finish!.Surplus);
        return (s, runA, runB);
    }

    [Fact]
    public void Reject_of_an_earlier_run_takes_back_its_moved_cells_not_its_dice()
    {
        // 7 cells moved against the surplus 2: the finish goes, back 5 — to c3 (by the dice, 9 − 2 = 7, it would be c1)
        var (s, runA, _) = Setup();
        var vasya = s.PlayerId("Вася");

        s.Act(new RejectProof(runA, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(vasya, -9, PointsReason.ProofRejected, runA), s.LastEvents<PointsChanged>().Single());
        Assert.Equal(vasya, Assert.Single(s.LastEvents<PlayerFinishRevoked>()).PlayerId);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, "c3", MoveReason.ProofRejected, (Guid?)runA), (moved.From, moved.To, moved.Reason, moved.RunId));
        Assert.Equal(["c7", "c6", "c5", "c4", "c3"], moved.Path);
        Assert.Equal("c3", s.Player("Вася").CellId);
        Assert.Null(FinishLine.First(s.State));
    }

    [Fact]
    public void Correction_down_of_an_earlier_run_takes_back_only_the_moved_cells_beyond_the_new_sum()
    {
        // 12 → 6 hours keeps 2 + 2 = 4: max(0, 7 − 4) = 3 cells against the surplus 2 — back 1 to c7 (by the dice, 9 − 4 = 5
        // against 2, it would be back 3 to c5)
        var (s, runA, _) = Setup();
        var vasya = s.PlayerId("Вася");

        s.Act(new CorrectRunHours(runA, 6, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(vasya, -5, PointsReason.RunCorrection, runA), s.LastEvents<PointsChanged>().Single());
        Assert.Single(s.LastEvents<PlayerFinishRevoked>());
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, "c7", MoveReason.RunCorrection, (Guid?)runA), (moved.From, moved.To, moved.Reason, moved.RunId));
        Assert.Equal(["c7"], moved.Path);
    }

    [Fact]
    public void Correction_down_of_an_earlier_run_still_covering_its_cells_moves_nothing()
    {
        // 12 → 9 hours drops the added 4, keeping 2 + 2 + 1 = 5: max(0, 7 − 5) = 2 ≤ surplus 2 — the finish stands, surplus 0
        var (s, runA, _) = Setup();
        var vasya = s.PlayerId("Вася");

        s.Act(new CorrectRunHours(runA, 9, Hours));

        ScenarioAssert.Accepted(s);
        Assert.Equal(-4, s.LastEvents<PointsChanged>().Single().Delta);
        Assert.Equal([new FinishSurplusChanged(vasya, -2)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<PlayerFinishRevoked>());
        Assert.Equal((LinearMap.FinishId, 0), (s.Player("Вася").CellId, s.Player("Вася").Finish!.Surplus));
    }

    [Fact]
    public void Earlier_run_moved_as_many_cells_as_its_dice_loses_them_one_for_one()
    {
        // The plain case: A moved 5 with dice 5 (2 + 2 + 1); B 4 + 4 + 4 from c5 — 3 steps to the finish, surplus 9.
        // A made easy: ⌈2·2/4⌉ + ⌈2·2/4⌉ + ⌈1·2/4⌉ = 3 → 2 cells against the surplus: surplus 7
        var s = Scenario.New().WithMapLength(8).WithCategory("Horror").WithGame("Silent Hill", 9, "Horror").WithGame("Alan Wake", 9, "Horror");
        s.WithPlayers("Вася");
        var runA = ProofSetup.CompleteRun(s, "Вася", [2, 2, 1]);
        ProofSetup.CompleteRun(s, "Вася", [4, 4, 4]);
        Assert.Equal(9, s.Player("Вася").Finish!.Surplus);

        s.Act(new ChangeRunDifficulty(runA, Difficulty.Easy, "По пруфу — лёгкая"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new FinishSurplusChanged(s.PlayerId("Вася"), -2)], s.LastEvents<FinishSurplusChanged>());
        Assert.Equal(7, s.Player("Вася").Finish!.Surplus);
    }
}
