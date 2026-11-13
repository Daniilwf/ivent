using GameEvent.Engine.Economy;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// Findings of the review of task 5.11 (D-741): the term of a request is due on its author's timer; a response no accept
/// could take is refused at once — the run ended or took a partner while the board was off, the author is inactive or became
/// the first finisher; a request past its term closes as timed out whatever closes it first.
/// </summary>
public class PartnerBoardReviewTests
{
    [Fact]
    public void The_term_of_an_open_request_is_due_on_its_authors_timer_and_only_while_the_board_is_on()
    {
        var s = PartnerBoardSteps.Board(r => r.WithCoop(inviteTtlHours: 100), requestTtlHours: 6);
        s.PlayTitle("Вася", CoopGame);
        Assert.Null(Timers.Next(s.State, s.Player("Вася")));
        var request = s.Post("Вася");

        Assert.Equal(s.Request(request).ExpiresAt, Timers.Next(s.State, s.Player("Вася")));

        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });
        Assert.Null(Timers.Next(s.State, s.Player("Вася")));
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = true } });

        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), request));
        ScenarioAssert.Accepted(s);
        Assert.Null(Timers.Next(s.State, s.Player("Вася")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_response_to_a_request_whose_run_ended_or_took_a_partner_while_the_board_was_off_is_refused(bool ended)
    {
        // D-420: with the board off nothing closes the request; switched back on, a response checks the run again
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Маша", request);
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });
        if (ended)
        {
            s.CompleteWith("Вася", 1, 1);
        }
        else
        {
            s.Accept("Вася", response);
        }

        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = true } });

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), ended ? RejectionCodes.CoopRunNotActive : RejectionCodes.CoopAlreadyJoined);
    }

    [Fact]
    public void A_response_to_an_inactive_author_is_refused_and_an_inactive_player_does_not_post()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.PlayTitle("Маша", SecondCoopGame);
        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true));
        ScenarioAssert.Accepted(s);
        s.Act(new SetPlayerInactive(s.PlayerId("Маша"), true));
        ScenarioAssert.Accepted(s);

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.CoopRunNotActive);
        s.Rejects(new PostPartnerRequest(s.PlayerId("Маша")), RejectionCodes.CoopRunNotActive);
    }

    [Fact]
    public void A_response_to_an_author_who_became_the_first_finisher_is_refused()
    {
        // Коля finishes first, Вася second; Вася posts on his «Portal 2». Коля's run is rejected: Вася is first now (D-713)
        var s = PartnerBoardSteps.Board(mapLength: 4);
        var kolyasRun = s.CompleteTitle("Коля", SoloGame, 3, 1);
        s.CompleteTitle("Вася", OtherSoloGame, 3, 1);
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Act(new RejectProof(kolyasRun, "На скрине другая игра"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.CoopFirstFinisher);
    }

    [Fact]
    public void A_request_past_its_term_closes_as_timed_out_when_its_run_ends_before_the_timer()
    {
        var s = PartnerBoardSteps.Board(requestTtlHours: 2);
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(3));

        s.NextRandom(1, 1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(PartnerRequestCloseReason.Timeout, Assert.Single(s.LastEvents<PartnerRequestClosed>(), c => c.RequestId == request).Reason);
    }
}
