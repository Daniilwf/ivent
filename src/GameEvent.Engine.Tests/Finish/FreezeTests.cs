using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// When the first finisher is frozen (P4, SE6, SE7; SPEC «Финиш подтверждается пруфом»; Q-3; the freeze amendment;
/// D-99): with <c>finish.requireApprovalForFirst</c> the first place becomes final when the proofs of ALL his runs up to
/// and including the finishing one are approved — <see cref="PlayerFrozen"/> follows the <see cref="ProofApproved"/> that
/// completes the set, in the same command. These runs go on top of the proof queue. Approving a run after the finish, or
/// the runs of a finisher who is not first, freezes nobody.
/// </summary>
public class FreezeTests
{
    [Fact]
    public void Approving_the_only_run_up_to_the_finish_freezes_the_first()
    {
        var s = New();
        var vasya = s.PlayerId("Вася");
        var (runId, at) = Complete(s, "Вася", [3, 1]);

        Approve(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new ProofApproved(runId, vasya, true, "Видел на стриме", s.Clock.UtcNow), new PlayerFrozen(vasya)],
            s.Last.Events);
        Assert.Equal(new FinishState(1, runId, at, Frozen: true, Bonus: 0, Surplus: 0), FinishOf(s, "Вася"));
        Assert.Equal(vasya, FinishLine.First(s.State));

        // Frozen as he was: nothing is taken or given by the freeze
        Assert.Equal((4, CoinsPerRun, LinearMap.FinishId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void Approving_the_finishing_run_with_an_earlier_run_unchecked_does_not_freeze()
    {
        // 1 + 1 → c2 (run A), 1 + 1 → the finish (run B); B is approved, A waits
        var s = New();
        Complete(s, "Вася", [1, 1]);
        var (runB, _) = Complete(s, "Вася", [1, 1]);

        Approve(s, runB);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.False(FinishOf(s, "Вася")!.Frozen);
    }

    [Fact]
    public void Approving_an_earlier_run_with_the_finishing_one_unchecked_does_not_freeze()
    {
        var s = New();
        var (runA, _) = Complete(s, "Вася", [1, 1]);
        Complete(s, "Вася", [1, 1]);

        Approve(s, runA);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.False(FinishOf(s, "Вася")!.Frozen);
    }

    [Fact]
    public void Approval_that_completes_the_runs_up_to_the_finish_freezes()
    {
        // B (the finishing run) first, then A: the freeze follows A's approval
        var s = New();
        var vasya = s.PlayerId("Вася");
        var (runA, _) = Complete(s, "Вася", [1, 1]);
        var (runB, _) = Complete(s, "Вася", [1, 1]);
        Approve(s, runB);
        ScenarioAssert.Accepted(s);

        Approve(s, runA);

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(ProofApproved), typeof(PlayerFrozen)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new PlayerFrozen(vasya), s.Last.Events[^1]);
        Assert.True(FinishOf(s, "Вася")!.Frozen);
    }

    [Fact]
    public void Rejected_run_before_the_finish_does_not_hold_the_freeze_back()
    {
        // A 1 + 1, B 4 + 4 (6 burned); A rejected (absorbed by the surplus): B alone is up to the finish now
        var s = New();
        var (runA, _) = Complete(s, "Вася", [1, 1]);
        var (runB, _) = Complete(s, "Вася", [4, 4]);
        Reject(s, runA);
        ScenarioAssert.Accepted(s);
        Assert.NotNull(FinishOf(s, "Вася"));

        Approve(s, runB);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFrozen(s.PlayerId("Вася")), s.Last.Events[^1]);
    }

    [Fact]
    public void Approving_a_run_completed_after_the_finish_does_not_freeze()
    {
        var s = New();
        FinishRun(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);

        Approve(s, later);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.False(FinishOf(s, "Вася")!.Frozen);
    }

    [Fact]
    public void Approving_the_finishing_run_of_a_later_finisher_freezes_nobody()
    {
        var s = New();
        FinishRun(s, "Вася");
        var petyaRun = FinishRun(s, "Петя");

        Approve(s, petyaRun);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.False(FinishOf(s, "Петя")!.Frozen);
        Assert.False(FinishOf(s, "Вася")!.Frozen);
    }

    [Fact]
    public void Approving_without_a_run_reaching_the_finish_freezes_nobody()
    {
        var s = New();
        var (runId, _) = Complete(s, "Вася", [1, 1]);

        Approve(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.Null(FinishOf(s, "Вася"));
    }

    [Fact]
    public void Approval_at_a_lower_difficulty_takes_from_the_surplus_and_then_freezes()
    {
        // Hard 6 + 6 = 12 on a map of 4: surplus 8. Normal by the proof: ⌈6·4/6⌉ × 2 = 8 → −4 points, surplus 4, no move
        var s = New();
        var vasya = s.PlayerId("Вася");
        var (runId, _) = Complete(s, "Вася", [6, 6], Difficulty.Hard);
        Assert.Equal(8, FinishOf(s, "Вася")!.Surplus);

        s.Act(new ApproveProof(runId, Difficulty.Normal, "На скрине нормальная"));

        ScenarioAssert.Accepted(s);
        Assert.IsType<RunDifficultyChanged>(s.Last.Events[0]);
        Assert.Equal([typeof(ProofApproved), typeof(PlayerFrozen)], s.Last.Events.TakeLast(2).Select(e => e.GetType()));
        Assert.Equal(new PointsChanged(vasya, -4, PointsReason.RunCorrection, runId), s.LastEvents<PointsChanged>().Single());
        Assert.Equal(new FinishSurplusChanged(vasya, -4), Assert.Single(s.LastEvents<FinishSurplusChanged>()));
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal((8, LinearMap.FinishId), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Equal((true, 4), (FinishOf(s, "Вася")!.Frozen, FinishOf(s, "Вася")!.Surplus));
    }

    [Fact]
    public void Frozen_player_is_frozen_once()
    {
        // After the freeze, approving his later runs writes no second freeze
        var s = New();
        FrozenFirst(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);

        Approve(s, later);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.Single(s.Log.OfType<PlayerFrozen>());
    }

    [Fact]
    public void Freeze_touches_only_the_first()
    {
        var s = New();
        var runId = FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        Complete(s, "Маша", [1, 1]);
        var others = s.State.Players.Values.Where(p => p.PlayerId != s.PlayerId("Вася")).ToList();

        Approve(s, runId);

        Assert.All(others, p => Assert.Equal(p, s.State.Players[p.PlayerId]));
        Assert.All(s.Last.Events, e => Assert.True(e is ProofApproved or PlayerFrozen, $"Unexpected {e}."));
    }

    // ---- The proof queue (SE6, Q-3) ----

    [Fact]
    public void All_runs_up_to_the_finish_go_on_top_of_the_proof_queue()
    {
        // Петя completes P first; then Вася A (1 + 1 → c2) and B (finish), then a run L after the finish
        var s = New();
        var (petyaRun, _) = Complete(s, "Петя", [1, 1]);
        var (runA, _) = Complete(s, "Вася", [1, 1]);
        var (runB, _) = Complete(s, "Вася", [1, 1]);
        var (later, _) = Complete(s, "Вася", [1, 1]);

        // Then A and B (by completion time) come before Петя's earlier run; L waits in the usual order
        Assert.Equal([runA, runB, petyaRun, later], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Runs_up_to_a_revoked_finish_leave_the_top_of_the_queue()
    {
        // Петя P, Вася A (c2) and B (the finish, surplus 0); A is rejected: the finish goes, B is an ordinary run again
        var s = New();
        var (petyaRun, _) = Complete(s, "Петя", [1, 1]);
        var (runA, _) = Complete(s, "Вася", [1, 1]);
        var (runB, _) = Complete(s, "Вася", [1, 1]);
        Reject(s, runA);
        ScenarioAssert.Accepted(s);
        Assert.Null(FinishOf(s, "Вася"));

        Assert.Equal([petyaRun, runB], ProofReviewOrder.Order(s.State));
    }
}
