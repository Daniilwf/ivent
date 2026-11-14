using GameEvent.Engine.Economy;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// Findings of the review of task 5.10 (D-711): an undo of the run an invitation names waits for it, a drop or tech reroll
/// with coop switched off ends the coop as usual (D-715), a partner who became the first finisher is out at the completion, a bet on the
/// run or an inactive roller stops an accept, and an invitation past its time does not count against the limits.
/// </summary>
public class CoopReviewTests
{
    [Fact]
    public void An_undo_of_the_start_waits_for_the_invitation_to_the_run()
    {
        var s = New();
        s.RollTitle("Вася", CoopGame).Start("Вася");
        var start = s.LastCommandId;
        s.Invite("Вася", "Петя");
        var invite = s.LastCommandId;

        s.Rejects(new UndoCommand(start, "Ошибка админа"), RejectionCodes.UndoDependents);
        Assert.Contains(invite, s.Last.Rejection!.Related);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_coop_switched_off_the_rollers_drop_or_tech_reroll_ends_the_coop_as_usual(bool drop)
    {
        // D-715, decided by the owner 2026-11-14: a coop in progress goes on as coop with the flag off, so its end is logged
        // as with the flag on (was: nothing of coop was written, the partner was freed silently)
        var s = New();
        var run = s.CoopRun();
        s.WithRuleset(r => r with { Features = r.Features with { Coop = false } });

        s.Act(drop ? new DropRun(s.PlayerId("Вася")) : new TechReroll(s.PlayerId("Вася"), TechRerollReason.WeakPc, Comment: null));

        ScenarioAssert.Accepted(s);
        var left = Assert.Single(s.LastEvents<CoopLeft>());
        Assert.Equal(
            (run, s.PlayerId("Петя"), drop ? CoopLeaveReason.RollerDropped : CoopLeaveReason.RollerTechRerolled),
            (left.RunId, left.PlayerId, left.Reason));
        Assert.Equal((TurnPhase.Idle, (Guid?)null), (s.Player("Петя").Phase, s.Player("Петя").ActiveRunId));
    }

    [Fact]
    public void A_partner_who_became_the_first_finisher_is_out_at_the_completion_and_the_roller_takes_the_whole_throw()
    {
        // Коля finishes first, Петя second; Петя joins Вася's «Portal 2». Коля's run is rejected: Петя is first now
        var s = New(mapLength: 4);
        var kolyasRun = s.CompleteTitle("Коля", SoloGame, 3, 1);
        s.CompleteTitle("Петя", OtherSoloGame, 3, 1);
        var run = s.CoopRun();
        s.Act(new RejectProof(kolyasRun, "На скрине другая игра"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(s.PlayerId("Петя"), FinishLine.First(s.State));
        var petya = s.Player("Петя");

        s.NextRandom(1, 1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(CoopLeaveReason.PartnerFirst, Assert.Single(s.LastEvents<CoopLeft>(), l => l.RunId == run).Reason);
        Assert.Empty(s.LastEvents<CoopRewardSplit>());
        Assert.Equal((petya.Points, petya.Coins), (s.Player("Петя").Points, s.Player("Петя").Coins));
    }

    [Fact]
    public void A_player_with_an_open_bet_on_the_run_does_not_join_it()
    {
        var s = New(r => r with { Features = r.Features with { Bets = true } });
        s.CompleteTitle("Петя", SoloGame, 1, 1);
        s.PlayTitle("Вася", CoopGame);
        var invitation = s.Invite("Вася", "Петя");
        s.Act(new PlaceBet(s.PlayerId("Петя"), s.PlayerId("Вася"), s.Ruleset.Bets.DeadlineOptionsDays[0], 1));
        ScenarioAssert.Accepted(s);

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), invitation, Accept: true), RejectionCodes.CoopPartnerHasBet);
    }

    [Fact]
    public void The_partner_of_a_coop_run_cannot_bet_on_it()
    {
        var s = New(r => r with { Features = r.Features with { Bets = true } });
        s.CompleteTitle("Петя", SoloGame, 1, 1);
        s.CoopRun();

        s.Rejects(new PlaceBet(s.PlayerId("Петя"), s.PlayerId("Вася"), s.Ruleset.Bets.DeadlineOptionsDays[0], 1), RejectionCodes.BetOnSelf);
    }

    [Fact]
    public void An_invitation_of_a_roller_marked_inactive_cannot_be_accepted()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var invitation = s.Invite("Вася", "Петя");
        s.Act(new Engine.Players.SetPlayerInactive(s.PlayerId("Вася"), true));
        ScenarioAssert.Accepted(s);

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), invitation, Accept: true), RejectionCodes.CoopRunNotActive);
    }

    [Fact]
    public void An_invitation_past_its_time_does_not_hold_the_run_before_the_timer_fired()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        s.Invite("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(25));

        s.Invite("Вася", "Маша");
    }

    [Fact]
    public void The_undo_of_the_completion_waits_for_the_rollers_later_command_too()
    {
        var s = New();
        s.CoopRun();
        s.CompleteWith("Вася", 3, 4);
        var completion = s.LastCommandId;
        s.RollTitle("Вася", SoloGame);
        var roll = s.LastCommandId;

        s.Rejects(new UndoCommand(completion, "Ошибка админа"), RejectionCodes.UndoDependents);
        Assert.Contains(roll, s.Last.Rejection!.Related);
    }
}
