using GameEvent.Engine.Economy;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// How a request of the partner board ends beside the board's own commands, and what the flags, the deadline and undo do
/// with it (EV32, EV35, EV37, EV38; D-420, D-516, D-524, D-712, D-740): the end of its run (completion, drop, tech reroll)
/// closes it with <see cref="PartnerRequestCloseReason.RunEnded"/> together with the run's pending responses; the season
/// going to «Подведение итогов» closes it with <see cref="PartnerRequestCloseReason.Deadline"/>; past the deadline nobody
/// posts or responds; with <c>features.partnerBoard</c> off nobody posts or responds, but an open request still closes by
/// its author, its term, the end of its run and a coop (D-743, decided by the owner 2026-11-14: the flag off refuses only
/// new posts and responses); an accepted response holds the undo of the
/// response, the undo of the author's close brings back the responses it cancelled (D-742). Scenarios S5.11-05 (run end), -06, -07, -09.
/// </summary>
public class PartnerBoardEndTests
{
    // ---- The run ends (S5.11-05) ----

    [Theory]
    [InlineData("complete")]
    [InlineData("drop")]
    [InlineData("techReroll")]
    public void The_end_of_the_run_closes_its_request_and_burns_its_responses(string end)
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var fromPetya = s.Respond("Петя", request);
        var fromMasha = s.Respond("Маша", request);
        var now = s.Clock.UtcNow;

        switch (end)
        {
            case "complete":
                s.NextRandom(1, 1).Complete("Вася");
                break;
            case "drop":
                s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
                break;
            default:
                s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
                break;
        }

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PartnerRequestClosed(request, PartnerRequestCloseReason.RunEnded, now),
            Assert.Single(s.LastEvents<PartnerRequestClosed>()));
        Assert.Equal(
            new[] { fromPetya, fromMasha }.Order(),
            s.LastEvents<InteractionExpired>().Where(e => e.Reason == InteractionExpiry.RunEnded).Select(e => e.InteractionId).Order());
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(fromPetya).Status);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(fromMasha).Status);
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Коля"), request), RejectionCodes.PartnerRequestNotOpen);
        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), fromPetya, Accept: true), RejectionCodes.InteractionNotPending);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void The_end_of_another_run_leaves_the_request_open()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.PlayTitle("Маша", SoloGame);

        s.CompleteWith("Маша", 1, 1);

        Assert.Empty(s.LastEvents<PartnerRequestClosed>());
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);
    }

    // ---- The deadline (S5.11-07, EV37, D-516) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_the_season_closes_open_requests_and_burns_their_responses(bool byTheDeadlineTimer)
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        s.PlayTitle("Маша", SecondCoopGame);
        var closedBefore = s.Post("Маша");
        s.Act(new ClosePartnerRequest(s.PlayerId("Маша"), closedBefore));
        ScenarioAssert.Accepted(s);
        if (byTheDeadlineTimer)
        {
            s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(3)));
            ScenarioAssert.Accepted(s);
            s.Advance(TimeSpan.FromHours(3));
        }

        var now = s.Clock.UtcNow;

        s.Act(byTheDeadlineTimer ? new ReachDeadline() : (ICommand)new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonStatus.Closing, s.State.Status);
        Assert.Equal(
            new PartnerRequestClosed(request, PartnerRequestCloseReason.Deadline, now),
            Assert.Single(s.LastEvents<PartnerRequestClosed>()));
        var expired = Assert.Single(s.LastEvents<InteractionExpired>(), e => e.InteractionId == response);
        Assert.Equal(InteractionExpiry.Deadline, expired.Reason);
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
    }

    [Fact]
    public void Past_the_deadline_of_a_season_still_active_nobody_posts_or_responds()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.PlayTitle("Маша", SecondCoopGame);
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(3)));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(3));
        Assert.Equal(SeasonStatus.Active, s.State.Status);

        s.Rejects(new PostPartnerRequest(s.PlayerId("Маша")), RejectionCodes.SeasonDeadlinePassed);
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.SeasonDeadlinePassed);
    }

    [Fact]
    public void In_closing_nobody_posts_or_responds()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.PlayTitle("Маша", SecondCoopGame);
        s.MoveStatusTo(SeasonStatus.Closing);

        s.Rejects(new PostPartnerRequest(s.PlayerId("Маша")), RejectionCodes.SeasonDeadlinePassed);
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.SeasonDeadlinePassed);
    }

    // ---- The flags (S5.11-06, EV35, D-420) ----

    [Fact]
    public void With_the_board_off_nobody_posts_or_responds()
    {
        // Coop on, the board off: the test ruleset as pinned plus coop. Closing is no new post or response (D-743): the
        // author's close of a request that is not there is refused as unknown, not by the flag
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var any = SequentialIds.Make(0x5C000000, 1);

        s.Rejects(new PostPartnerRequest(s.PlayerId("Вася")), RejectionCodes.FeatureDisabled);
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), any), RejectionCodes.FeatureDisabled);
        s.Rejects(new ClosePartnerRequest(s.PlayerId("Вася"), any), RejectionCodes.PartnerRequestUnknown);
    }

    [Fact]
    public void With_the_board_switched_off_an_open_request_takes_no_response_but_its_author_closes_it()
    {
        // D-743, decided by the owner 2026-11-14: the flag off refuses only new posts and responses (was: the author's close
        // was refused too, FeatureDisabled; changed with the decision, D-790)
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Маша", request);
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });
        var now = s.Clock.UtcNow;

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.FeatureDisabled);
        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), request));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PartnerRequestClosed(request, PartnerRequestCloseReason.Author, now), Assert.Single(s.LastEvents<PartnerRequestClosed>()));
        Assert.Equal(new InteractionExpired(response, InteractionExpiry.RequestClosed, now), Assert.Single(s.LastEvents<InteractionExpired>()));
    }

    [Fact]
    public void With_the_board_switched_off_an_open_request_times_out_and_burns_its_responses()
    {
        // D-743 with D-742: the term of a request runs whatever the flag (was: it stood still while the board was off); the
        // response lives longer than the request here, so it is the request's close that burns it
        var s = PartnerBoardSteps.Board(r => r.WithCoop(inviteTtlHours: 100));
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Маша", request);
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });
        Assert.Equal(s.Request(request).ExpiresAt, Timers.Next(s.State, s.Player("Вася")));

        s.Advance(TimeSpan.FromHours(49));
        s.Act(new FireTimers());

        ScenarioAssert.Accepted(s);
        var now = s.Clock.UtcNow;
        Assert.Equal(new PartnerRequestClosed(request, PartnerRequestCloseReason.Timeout, now), Assert.Single(s.LastEvents<PartnerRequestClosed>()));
        Assert.Equal(InteractionExpiry.RequestClosed, Assert.Single(s.LastEvents<InteractionExpired>(), e => e.InteractionId == response).Reason);
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(response).Status);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("drop")]
    [InlineData("techReroll")]
    public void With_the_board_switched_off_the_end_of_the_run_still_closes_its_request_and_burns_its_responses(string end)
    {
        // D-743, decided by the owner 2026-11-14: the flag off refuses only new posts and responses
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });
        var now = s.Clock.UtcNow;

        switch (end)
        {
            case "complete":
                s.NextRandom(1, 1).Complete("Вася");
                break;
            case "drop":
                s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
                break;
            default:
                s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
                break;
        }

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PartnerRequestClosed(request, PartnerRequestCloseReason.RunEnded, now),
            Assert.Single(s.LastEvents<PartnerRequestClosed>()));
        Assert.Equal(InteractionExpiry.RunEnded, Assert.Single(s.LastEvents<InteractionExpired>(), e => e.InteractionId == response).Reason);
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
    }

    [Fact]
    public void With_the_board_switched_off_a_coop_still_closes_the_request_and_supersedes_the_other_responses()
    {
        // D-743: accepting a response is a coop answer (coop is on), and the coop closes the request as with the board on
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var fromPetya = s.Respond("Петя", request);
        var fromMasha = s.Respond("Маша", request);
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });
        var now = s.Clock.UtcNow;

        s.Accept("Вася", fromMasha);

        Assert.Equal(s.PlayerId("Маша"), s.State.Runs[run].PartnerId);
        Assert.Equal(new PartnerRequestClosed(request, PartnerRequestCloseReason.Coop, now), Assert.Single(s.LastEvents<PartnerRequestClosed>()));
        Assert.Equal(InteractionExpiry.Superseded, Assert.Single(s.LastEvents<InteractionExpired>(), e => e.InteractionId == fromPetya).Reason);
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
    }

    [Fact]
    public void With_the_board_switched_off_a_direct_invitation_accepted_closes_the_request_too()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        var direct = s.Invite("Вася", "Маша");
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });

        s.Accept("Маша", direct);

        Assert.Equal(PartnerRequestCloseReason.Coop, Assert.Single(s.LastEvents<PartnerRequestClosed>()).Reason);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(response).Status);
    }

    [Fact]
    public void Switched_back_on_a_request_closed_by_its_term_meanwhile_stays_closed()
    {
        // D-743: the term ran out while the board was off and the timer closed it then (was: it waited for the board to come
        // back and closed at the first timer after, D-420); switched on again, nothing is left to close
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });
        s.Advance(TimeSpan.FromHours(49));
        s.Act(new FireTimers());
        ScenarioAssert.Accepted(s);
        var closed = Assert.Single(s.LastEvents<PartnerRequestClosed>());
        Assert.Equal((request, PartnerRequestCloseReason.Timeout), (closed.RequestId, closed.Reason));
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = true } });

        s.Rejects(new FireTimers(), RejectionCodes.TimersNothingDue);
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
    }

    // ---- Undo (S5.11-09, EV38) ----

    [Fact]
    public void The_undo_of_a_response_whose_invitation_was_accepted_is_refused_and_names_the_accept()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        var respond = s.LastCommandId;
        s.Accept("Вася", response);
        var accept = s.LastCommandId;

        s.Rejects(new UndoCommand(respond, "Ошибка админа"), RejectionCodes.UndoDependents);
        Assert.Contains(accept, s.Last.Rejection!.Related);
    }

    [Fact]
    public void The_undo_of_an_unanswered_response_removes_its_invitation()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var before = s.State;
        var response = s.Respond("Петя", request);
        var respond = s.LastCommandId;

        s.Act(new UndoCommand(respond, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.False(s.State.Interactions.ContainsKey(response));
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);

        // Nothing of his waits on the request now: he may respond again
        s.Respond("Петя", request);
    }

    [Fact]
    public void The_undo_of_a_post_with_nothing_after_it_removes_the_request()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var before = s.State;
        var request = s.Post("Вася");
        var post = s.LastCommandId;

        s.Act(new UndoCommand(post, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.False(s.State.PartnerRequests.ContainsKey(request));

        // The run takes a request again
        s.Post("Вася");
    }

    [Fact]
    public void The_undo_of_a_post_waits_for_the_responses_to_it()
    {
        // Invariant 12: the response names the request, so it depends on the post
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var post = s.LastCommandId;
        s.Respond("Петя", request);
        var respond = s.LastCommandId;

        s.Rejects(new UndoCommand(post, "Ошибка админа"), RejectionCodes.UndoDependents);
        Assert.Contains(respond, s.Last.Rejection!.Related);
    }

    [Fact]
    public void The_undo_of_the_accept_reopens_the_request_and_its_superseded_responses()
    {
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var fromPetya = s.Respond("Петя", request);
        var fromMasha = s.Respond("Маша", request);
        var before = s.State;
        s.Accept("Вася", fromMasha);
        var accept = s.LastCommandId;

        s.Act(new UndoCommand(accept, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);
        Assert.Equal(InteractionStatus.Pending, s.Invitation(fromPetya).Status);
        Assert.Equal(InteractionStatus.Pending, s.Invitation(fromMasha).Status);
        Assert.Null(s.State.Runs[run].Coop);
        Assert.Equal(TurnPhase.Idle, s.Player("Маша").Phase);
    }

    [Fact]
    public void The_undo_of_the_authors_close_reopens_the_request()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var before = s.State;
        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), request));
        ScenarioAssert.Accepted(s);
        var close = s.LastCommandId;

        s.Act(new UndoCommand(close, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);
        s.Respond("Петя", request);
    }

    [Fact]
    public void The_undo_of_the_authors_close_brings_back_the_responses_it_cancelled()
    {
        // D-742 with invariant 12: the close cancelled the pending responses, its undo returns them to pending
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        var before = s.State;
        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), request));
        ScenarioAssert.Accepted(s);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(response).Status);
        var close = s.LastCommandId;

        s.Act(new UndoCommand(close, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal(InteractionStatus.Pending, s.Invitation(response).Status);
        s.Accept("Вася", response);
    }
}
