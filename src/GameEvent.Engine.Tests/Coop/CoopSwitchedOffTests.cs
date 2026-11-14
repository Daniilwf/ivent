using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// <c>features.coop</c> switched off mid-season (Н-25, D-715, decided by the owner 2026-11-14): a coop run already in
/// progress goes on as coop to its end — the throw is split, each moves his share, its end and its reject are logged as
/// with the flag on; nobody starts a new coop — an invitation is not sent, a pending one (a direct invitation or a response
/// of the partner board) is not accepted and burns by its own time. Written before the implementation.
/// </summary>
public class CoopSwitchedOffTests
{
    private static Ruleset Off(Ruleset r) => r with { Features = r.Features with { Coop = false } };

    // ---- Nobody starts a new coop ----

    [Fact]
    public void With_the_flag_switched_off_the_roller_of_a_running_coop_game_does_not_invite()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        s.WithRuleset(Off);

        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void With_the_flag_switched_off_a_pending_invitation_is_not_accepted()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.WithRuleset(Off);

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.FeatureDisabled);

        Assert.Equal(InteractionStatus.Pending, s.Invitation(id).Status);
        Assert.Null(s.State.Runs[run].Coop);
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
    }

    [Fact]
    public void With_coop_and_the_board_switched_off_a_pending_response_is_not_accepted_and_nobody_responds()
    {
        // The board needs coop: both go off together. A response is a coop invitation — accepting it would start a coop
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        s.WithRuleset(r => r with { Features = r.Features with { Coop = false, PartnerBoard = false } });

        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), response, Accept: true), RejectionCodes.FeatureDisabled);
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Маша"), request), RejectionCodes.FeatureDisabled);

        Assert.Null(s.State.Runs[run].Coop);
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
    }

    [Fact]
    public void Switched_back_on_a_pending_invitation_still_in_its_time_is_accepted()
    {
        // The boundary: the flag only stops new coops while it is off; an invitation that did not burn meanwhile is good again
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.WithRuleset(Off);
        s.Advance(TimeSpan.FromHours(1));
        s.WithRuleset(r => r with { Features = r.Features with { Coop = true } });

        s.Accept("Петя", id);

        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);
    }

    // ---- A coop in progress goes on as coop ----

    [Fact]
    public void With_the_flag_switched_off_a_partner_marked_inactive_leaves_the_coop_as_usual()
    {
        var s = New();
        var run = s.CoopRun();
        var petya = s.PlayerId("Петя");
        s.WithRuleset(Off);
        var now = s.Clock.UtcNow;

        s.Act(new SetPlayerInactive(petya, IsInactive: true));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new CoopLeft(run, petya, CoopLeaveReason.PartnerInactive, now), Assert.Single(s.LastEvents<CoopLeft>()));
        Assert.Null(s.State.Runs[run].Coop);
        Assert.Equal((TurnPhase.Idle, (Guid?)null), (s.Player("Петя").Phase, s.Player("Петя").ActiveRunId));
    }

    [Fact]
    public void With_the_flag_switched_off_the_partner_still_moves_by_his_share()
    {
        var s = New();
        var run = s.CoopRun();
        var petya = s.PlayerId("Петя");
        s.WithRuleset(Off);

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Contains(new PointsChanged(petya, 3, PointsReason.CompletionRoll, run), s.Last.Events);
        Assert.Contains(new PlayerMoved(petya, LinearMap.StartId, "c3", 3, ["c1", "c2", "c3"], MoveReason.CompletionRoll, run), s.Last.Events);
        Assert.Contains(new CoinsChanged(petya, 3, CoinsReason.CompletionReward, run), s.Last.Events);
    }

    [Fact]
    public void With_the_flag_switched_off_a_reject_of_the_coop_run_takes_from_each_his_share()
    {
        var s = New();
        var run = s.CoopRun();
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));
        s.WithRuleset(Off);
        s.CompleteWith("Вася", 3, 4);

        s.Act(new RejectProof(run, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new PointsChanged(vasya, -4, PointsReason.ProofRejected, run), s.Last.Events);
        Assert.Contains(new PointsChanged(petya, -3, PointsReason.ProofRejected, run), s.Last.Events);
        Assert.Contains(new CoinsChanged(petya, -3, CoinsReason.ProofRejected, run), s.Last.Events);
        foreach (var player in new[] { "Вася", "Петя" })
        {
            Assert.Equal((0, 0, LinearMap.StartId), (s.Player(player).Points, s.Player(player).Coins, s.Player(player).CellId));
        }
    }

    [Fact]
    public void A_solo_run_completed_with_the_flag_off_is_not_split()
    {
        // The boundary: the flag off changes nothing for a run without a partner
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        s.WithRuleset(Off);

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<CoopRewardSplit>());
        Assert.Equal(7, s.Player("Вася").Points);
        Assert.Equal(SeasonEngine.Replay(s.Log), s.State);
    }
}
