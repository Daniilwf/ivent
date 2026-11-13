using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// The partner's finish by his share (EV56; D-545, D-99, Q-3): the coop run is among «the runs up to the finish» of both, each
/// finish is provisional until that run's proof is approved, each freezes by his own finish; when both reach the finish in
/// one completion the roller is first (his move is written first), the partner next; a reject takes the partner's finish
/// back as the roller's. A linear map of 4 steps: start, c1, c2, c3, finish. Scenario S5.10-15 of the stage 5 plan.
/// </summary>
public class CoopFinishTests
{
    private const int MapLength = 4;

    /// <summary>Петя stands on c2 (his own run 1 + 1), Вася plays «Portal 2» with him.</summary>
    private static (Scenario S, Guid PetyasRun, Guid Coop) PetyaTwoCellsFromTheFinish()
    {
        var s = New(mapLength: MapLength);
        var petyasRun = s.CompleteTitle("Петя", SoloGame, 1, 1);
        Assert.Equal("c2", s.Player("Петя").CellId);
        var coop = s.CoopRun();
        return (s, petyasRun, coop);
    }

    [Fact]
    public void A_partner_who_reaches_the_finish_by_his_share_finishes_provisionally()
    {
        // 3 + 3 = 6: Вася 3 → c3, Петя 3 from c2 → the finish, one step burns
        var (s, _, coop) = PetyaTwoCellsFromTheFinish();
        var petya = s.PlayerId("Петя");
        var at = s.Clock.UtcNow;

        s.NextRandom(3, 3).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFinished(petya, coop, 1, at, 1), Assert.Single(s.LastEvents<PlayerFinished>()));
        var events = s.Last.Events.ToList();
        Assert.True(
            events.FindIndex(e => e is PlayerMoved m && m.PlayerId == petya) < events.FindIndex(e => e is PlayerFinished),
            "The finish follows the partner's move.");
        Assert.Equal(LinearMap.FinishId, s.Player("Петя").CellId);
        Assert.Equal("c3", s.Player("Вася").CellId);
        var finish = s.Player("Петя").Finish!;
        Assert.Equal((1, coop, false), (finish.Order, finish.RunId, finish.Frozen));
        Assert.Equal(petya, FinishLine.First(s.State));
        Assert.True(s.State.Runs[coop].Coop!.ReachedFinish);
        Assert.False(s.State.Runs[coop].ReachedFinish);
    }

    [Fact]
    public void The_approval_of_the_coop_run_freezes_the_partner_who_finished_first_by_it()
    {
        var (s, petyasRun, coop) = PetyaTwoCellsFromTheFinish();
        s.CompleteWith("Вася", 3, 3);

        // His own earlier run approved: the finishing coop run still waits
        s.Act(new ApproveProof(petyasRun, null, "Видел на стриме"));
        ScenarioAssert.Accepted(s);
        Assert.False(s.Player("Петя").Finish!.Frozen);

        s.Act(new ApproveProof(coop, null, "Видел на стриме"));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new PlayerFrozen(s.PlayerId("Петя")), s.Last.Events);
        Assert.True(s.Player("Петя").Finish!.Frozen);
    }

    [Fact]
    public void A_reject_of_the_coop_run_takes_the_partners_finish_back()
    {
        var (s, _, coop) = PetyaTwoCellsFromTheFinish();
        s.CompleteWith("Вася", 3, 3);

        s.Act(new RejectProof(coop, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFinishRevoked(s.PlayerId("Петя"), coop), Assert.Single(s.LastEvents<PlayerFinishRevoked>()));
        Assert.Null(s.Player("Петя").Finish);
        Assert.Null(FinishLine.First(s.State));
        Assert.Equal((2, "c2"), (s.Player("Петя").Points, s.Player("Петя").CellId));
        Assert.Equal((0, LinearMap.StartId), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void When_both_finish_in_one_completion_the_roller_is_first_and_the_partner_next()
    {
        // 4 + 4 = 8: 4 each, both from the start reach the finish
        var s = New(mapLength: MapLength);
        var coop = s.CoopRun();
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));

        s.NextRandom(4, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var finished = s.LastEvents<PlayerFinished>().ToList();
        Assert.Equal([(vasya, 1), (petya, 2)], finished.Select(f => (f.PlayerId, f.Order)));
        Assert.All(finished, f => Assert.Equal(coop, f.RunId));
        Assert.Equal(vasya, FinishLine.First(s.State));
        Assert.Contains(s.LastEvents<PointsChanged>(), p => p.PlayerId == petya && p.Reason == PointsReason.FinishBonus && p.Delta == 10);
        Assert.False(s.Player("Вася").Finish!.Frozen);
    }

    [Fact]
    public void After_a_double_finish_the_approval_freezes_the_roller_who_is_first()
    {
        var s = New(mapLength: MapLength);
        var coop = s.CoopRun();
        s.CompleteWith("Вася", 4, 4);

        s.Act(new ApproveProof(coop, null, "Видел на стриме"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFrozen(s.PlayerId("Вася")), Assert.Single(s.LastEvents<PlayerFrozen>()));
        Assert.True(s.Player("Вася").Finish!.Frozen);
        Assert.False(s.Player("Петя").Finish!.Frozen);
    }

    [Fact]
    public void A_reject_of_the_run_both_finished_by_revokes_both_finishes()
    {
        var s = New(mapLength: MapLength);
        var coop = s.CoopRun();
        s.CompleteWith("Вася", 4, 4);

        s.Act(new RejectProof(coop, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new[] { s.PlayerId("Вася"), s.PlayerId("Петя") }.Order(),
            s.LastEvents<PlayerFinishRevoked>().Select(r => r.PlayerId).Order());
        Assert.Null(FinishLine.First(s.State));
        foreach (var player in new[] { "Вася", "Петя" })
        {
            Assert.Equal((0, 0, LinearMap.StartId), (s.Player(player).Points, s.Player(player).Coins, s.Player(player).CellId));
            Assert.Null(s.Player(player).Finish);
        }
    }
}
