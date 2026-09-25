using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// The first finish is provisional until the proofs are approved (P3; SPEC «Пока админ не одобрил прохождение, которое
/// довело до финиша, первое место предварительное. При реджекте игрок откатывается, а место достаётся следующему
/// финишировавшему»; D-15, Q-3, D-99). Rejecting a run up to the finish (the finishing one or an earlier one) takes back
/// its points and coins as any reject; its k steps come off the position: within the surplus (steps burned at the finish)
/// the surplus shrinks and the finish stands, beyond it the finish is revoked with <see cref="PlayerFinishRevoked"/> and
/// the token goes back k − surplus cells along the walked path. The player is active again and can finish later under a
/// new order number (orders are never reused).
/// </summary>
public class ProvisionalFinishTests
{
    [Fact]
    public void Rejecting_the_finishing_run_of_the_only_finisher_revokes_the_finish()
    {
        var s = New();
        var vasya = s.PlayerId("Вася");
        var runId = FinishRun(s, "Вася");

        Reject(s, runId);

        // The usual reject (points, the cells back to the start, coins), then the finish is revoked
        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(vasya, -4, PointsReason.ProofRejected, runId), s.LastEvents<PointsChanged>().Single());
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, LinearMap.StartId, MoveReason.ProofRejected), (moved.From, moved.To, moved.Reason));
        Assert.Equal(new CoinsChanged(vasya, -CoinsPerRun, CoinsReason.ProofRejected, runId), s.LastEvents<CoinsChanged>().Single());
        Assert.Equal(new PlayerFinishRevoked(vasya, runId), Assert.Single(s.LastEvents<PlayerFinishRevoked>()));
        Assert.True(IndexOf<PlayerFinishRevoked>(s) > IndexOf<ProofRejected>(s), "The revoke must follow the reject.");

        // The first had no bonus, so none is taken back
        Assert.Empty(BonusChanges(s));

        // Nobody is first, Вася is back on the start with nothing
        Assert.Null(FinishOf(s, "Вася"));
        Assert.Null(FinishLine.First(s.State));
        Assert.Equal((0, 0, LinearMap.StartId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void Rejecting_an_earlier_run_up_to_the_finish_revokes_the_finish_without_surplus()
    {
        // Run A 1 + 1 → c2, run B 1 + 1 → the finish (surplus 0). A is rejected: k = 2 > 0 — back 2 cells to c2
        var s = New();
        var vasya = s.PlayerId("Вася");
        var (runA, _) = Complete(s, "Вася", [1, 1]);
        var (runB, _) = Complete(s, "Вася", [1, 1]);

        Reject(s, runA);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(vasya, -2, PointsReason.ProofRejected, runA), s.LastEvents<PointsChanged>().Single());
        Assert.Equal(new CoinsChanged(vasya, -CoinsPerRun, CoinsReason.ProofRejected, runA), s.LastEvents<CoinsChanged>().Single());
        Assert.Equal(vasya, Assert.Single(s.LastEvents<PlayerFinishRevoked>()).PlayerId);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, "c2", MoveReason.ProofRejected, (Guid?)runA), (moved.From, moved.To, moved.Reason, moved.RunId));
        Assert.Equal(["c3", "c2"], moved.Path);
        Assert.Null(FinishOf(s, "Вася"));
        Assert.Null(FinishLine.First(s.State));
        Assert.Equal(RunStatus.Completed, s.State.Runs[runB].Status);
    }

    [Fact]
    public void Rejecting_an_earlier_run_within_the_surplus_keeps_the_finish()
    {
        // Run A 1 + 1 → c2, run B 4 + 4: two steps to the finish, six burn. A is rejected: k = 2 ≤ 6 — the surplus is 4
        var s = New();
        var vasya = s.PlayerId("Вася");
        var (runA, _) = Complete(s, "Вася", [1, 1]);
        var (runB, at) = Complete(s, "Вася", [4, 4]);
        Assert.Equal(6, FinishOf(s, "Вася")!.Surplus);

        Reject(s, runA);

        ScenarioAssert.Accepted(s);
        Assert.Equal(-2, s.LastEvents<PointsChanged>().Single().Delta);
        Assert.Equal([new FinishSurplusChanged(vasya, -2)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<PlayerFinishRevoked>());
        Assert.Equal(new FinishState(1, runB, at, Frozen: false, Bonus: 0, Surplus: 4, FinishBonusRules.Of(s.Ruleset.Finish), s.Ruleset.Finish.RequireApprovalForFirst), FinishOf(s, "Вася"));
        Assert.Equal((8, LinearMap.FinishId), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Rejecting_the_finishing_run_goes_back_by_its_steps_less_the_surplus()
    {
        // A 1 + 1 → c2, B 4 + 4 → the finish (surplus 6); B is rejected: k = 8, back 8 − 6 = 2 cells to c2
        var s = New();
        var vasya = s.PlayerId("Вася");
        Complete(s, "Вася", [1, 1]);
        var (runB, _) = Complete(s, "Вася", [4, 4]);

        Reject(s, runB);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFinishRevoked(vasya, runB), Assert.Single(s.LastEvents<PlayerFinishRevoked>()));
        Assert.Equal("c2", Assert.Single(s.LastEvents<PlayerMoved>()).To);
        Assert.Equal((2, "c2"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Rejecting_an_earlier_run_of_a_later_finisher_recalculates_the_bonuses()
    {
        // Вася 1; Петя: A 1 + 1, B 1 + 1 → second (+10); Маша third (+8). Петя's A is rejected: his finish goes (−10),
        // Маша is second now (8 → 10)
        var s = New();
        FinishRun(s, "Вася");
        var (petyaA, _) = Complete(s, "Петя", [1, 1]);
        Complete(s, "Петя", [1, 1]);
        FinishRun(s, "Маша");
        var (petya, masha) = (s.PlayerId("Петя"), s.PlayerId("Маша"));

        Reject(s, petyaA);

        ScenarioAssert.Accepted(s);
        Assert.Equal(petya, Assert.Single(s.LastEvents<PlayerFinishRevoked>()).PlayerId);
        Assert.Equal(
            [(masha, 2, PointsReason.FinishBonus), (petya, -10, PointsReason.FinishBonusRevoked)],
            BonusChanges(s).OrderByDescending(x => x.Delta));
        Assert.Equal((3, 10), (FinishOf(s, "Маша")!.Order, FinishOf(s, "Маша")!.Bonus));
        Assert.Equal("c2", s.Player("Петя").CellId);
    }

    [Fact]
    public void Revoked_player_moves_again()
    {
        var s = New();
        var runId = FinishRun(s, "Вася");
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        Complete(s, "Вася", [1, 1]);

        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.StartId, "c2"), (moved.From, moved.To));
        Assert.Empty(s.LastEvents<PlayerFinished>());
    }

    [Fact]
    public void Revoked_player_can_finish_again_under_a_new_order()
    {
        // Вася 1, Петя 2; Вася is revoked (Петя first); Вася finishes again: order 3, the second finisher's bonus
        var s = New();
        var vasya = s.PlayerId("Вася");
        var runId = FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        var (again, at) = Complete(s, "Вася", [3, 1]);

        Assert.Equal(new PlayerFinished(vasya, again, 3, at, 0), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.Equal([(vasya, 10, PointsReason.FinishBonus)], BonusChanges(s));
        Assert.Equal(new FinishState(3, again, at, Frozen: false, Bonus: 10, Surplus: 0, FinishBonusRules.Of(s.Ruleset.Finish), s.Ruleset.Finish.RequireApprovalForFirst), FinishOf(s, "Вася"));
        Assert.Equal(s.PlayerId("Петя"), FinishLine.First(s.State));
    }

    [Fact]
    public void Only_finisher_revoked_and_finishing_again_is_first_again()
    {
        // Orders are not reused: the first finish after the revoke is order 2, and it is the first place
        var s = New();
        var vasya = s.PlayerId("Вася");
        var runId = FinishRun(s, "Вася");
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        var (again, at) = Complete(s, "Вася", [3, 1]);

        Assert.Equal(new PlayerFinished(vasya, again, 2, at, 0), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.Empty(BonusChanges(s));
        Assert.Equal(vasya, FinishLine.First(s.State));
    }

    [Fact]
    public void Approved_first_finish_is_final_and_cannot_be_rejected()
    {
        // The boundary: once approved, the finishing run is checked and a reject is refused (D-98)
        var s = New();
        var runId = FrozenFirst(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, runId), RejectionCodes.ProofAlreadyReviewed);
    }

    [Fact]
    public void Revoke_touches_only_the_revoked_player_when_nobody_else_finished()
    {
        var s = New();
        var runId = FinishRun(s, "Вася");
        Complete(s, "Петя", [1, 1]);
        var petya = s.Player("Петя");

        Reject(s, runId);

        Assert.Equal(petya, s.Player("Петя"));
        Assert.All(s.Last.Events.OfType<PointsChanged>(), e => Assert.Equal(s.PlayerId("Вася"), e.PlayerId));
    }
}
