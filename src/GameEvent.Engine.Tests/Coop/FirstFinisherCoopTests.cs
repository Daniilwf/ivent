using GameEvent.Engine.Finish;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// The first finisher in coop (EV42, coop part; D-520, D-510, Н-18): provisional or frozen, he does not affect the race — he is
/// no partner and invites nobody (a share of his run's points to a partner would be his influence). A finisher not first is a
/// partner like anybody, with points and coins but no move (Q-3). A roller who becomes first while the coop runs completes
/// alone: the coop ends with <see cref="CoopLeaveReason.RollerFirst"/>, nothing is split (our default). A linear map of 4
/// steps: from the start 3 + 1 reaches the finish. Scenarios S5.10-08, -09, -16 of the stage 5 plan.
/// </summary>
public class FirstFinisherCoopTests
{
    private const int MapLength = 4;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_first_finisher_is_not_invited(bool frozen)
    {
        var s = New(mapLength: MapLength);
        var mashasRun = s.CompleteTitle("Маша", SoloGame, 3, 1);
        if (frozen)
        {
            s.Act(new ApproveProof(mashasRun, null, "Видел на стриме"));
            Assert.True(s.Player("Маша").Finish!.Frozen);
        }

        s.PlayTitle("Вася", CoopGame);

        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Маша")), RejectionCodes.CoopFirstFinisher);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_first_finisher_does_not_invite_to_his_free_mode_run(bool frozen)
    {
        var s = New(mapLength: MapLength);
        var mashasRun = s.CompleteTitle("Маша", SoloGame, 3, 1);
        if (frozen)
        {
            s.Act(new ApproveProof(mashasRun, null, "Видел на стриме"));
        }

        s.PlayTitle("Маша", CoopGame);

        s.Rejects(new InviteToCoop(s.PlayerId("Маша"), s.PlayerId("Петя")), RejectionCodes.CoopFirstFinisher);
    }

    [Fact]
    public void A_player_who_finished_first_after_the_invitation_cannot_accept_it()
    {
        var s = New(mapLength: MapLength);
        s.PlayTitle("Маша", SoloGame);
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Маша");
        s.CompleteWith("Маша", 3, 1);
        Assert.Equal(s.PlayerId("Маша"), FinishLine.First(s.State));

        s.Rejects(new AnswerInvitation(s.PlayerId("Маша"), id, Accept: true), RejectionCodes.CoopFirstFinisher);
    }

    [Fact]
    public void A_later_finisher_partner_gets_his_points_and_coins_but_does_not_move()
    {
        // S5.10-16: Маша first, Коля second; Коля is Вася's partner. 1 + 1 = 2: one each, coins 6: three each
        var s = New(mapLength: MapLength);
        s.CompleteTitle("Маша", SoloGame, 3, 1);
        s.CompleteTitle("Коля", OtherSoloGame, 3, 1);
        var kolya = s.PlayerId("Коля");
        var kolyaBefore = s.Player("Коля");
        var run = s.CoopRun(partner: "Коля");

        s.NextRandom(1, 1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Contains(new PointsChanged(kolya, 1, PointsReason.CompletionRoll, run), s.Last.Events);
        Assert.Contains(new CoinsChanged(kolya, 3, CoinsReason.CompletionReward, run), s.Last.Events);
        Assert.DoesNotContain(s.LastEvents<PlayerMoved>(), m => m.PlayerId == kolya);
        Assert.Equal(LinearMap.FinishId, s.Player("Коля").CellId);
        Assert.Equal((kolyaBefore.Points + 1, kolyaBefore.Coins + 3), (s.Player("Коля").Points, s.Player("Коля").Coins));
        Assert.Equal("c1", s.Player("Вася").CellId);
        Assert.True(s.State.Runs[run].Coop!.AfterFinish);
    }

    [Fact]
    public void A_roller_who_became_first_while_the_coop_ran_completes_alone()
    {
        // Коля finishes first, Вася second; Вася then plays «Portal 2» with Петя. Коля's finishing run is rejected: Вася is
        // first now, and the first finisher shares nothing — the coop ends (at the reject or at the completion), unsplit
        var s = New(mapLength: MapLength);
        var kolyasRun = s.CompleteTitle("Коля", SoloGame, 3, 1);
        s.CompleteTitle("Вася", OtherSoloGame, 3, 1);
        var run = s.CoopRun();
        var petya = s.PlayerId("Петя");
        var mark = s.Log.Count;
        var petyaBefore = s.Player("Петя");

        s.Act(new RejectProof(kolyasRun, "На скрине другая игра"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
        s.NextRandom(1, 1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var since = s.Log.Skip(mark).ToList();
        Assert.Equal(CoopLeaveReason.RollerFirst, Assert.Single(since.OfType<CoopLeft>(), l => l.RunId == run && l.PlayerId == petya).Reason);
        Assert.Empty(since.OfType<CoopRewardSplit>());
        Assert.DoesNotContain(since.OfType<PointsChanged>(), p => p.PlayerId == petya);
        Assert.DoesNotContain(since.OfType<CoinsChanged>(), c => c.PlayerId == petya);
        Assert.DoesNotContain(since.OfType<PlayerMoved>(), m => m.PlayerId == petya);
        var petyaAfter = s.Player("Петя");
        Assert.Equal((petyaBefore.Points, petyaBefore.Coins, petyaBefore.CellId), (petyaAfter.Points, petyaAfter.Coins, petyaAfter.CellId));
        Assert.Equal((TurnPhase.Idle, (Guid?)null), (petyaAfter.Phase, petyaAfter.ActiveRunId));
    }
}
