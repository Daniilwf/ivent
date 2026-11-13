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
/// posts or responds; with <c>features.partnerBoard</c> off every board command is refused and an open request waits
/// without timing out; an accepted response holds the undo of the response. Scenarios S5.11-05 (run end), -06, -07, -09.
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
    public void With_the_board_off_every_board_command_is_refused()
    {
        // Coop on, the board off: the test ruleset as pinned plus coop
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var any = SequentialIds.Make(0x5C000000, 1);

        s.Rejects(new PostPartnerRequest(s.PlayerId("Вася")), RejectionCodes.FeatureDisabled);
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), any), RejectionCodes.FeatureDisabled);
        s.Rejects(new ClosePartnerRequest(s.PlayerId("Вася"), any), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void With_the_board_switched_off_an_open_request_takes_no_command_and_does_not_time_out()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.FeatureDisabled);
        s.Rejects(new ClosePartnerRequest(s.PlayerId("Вася"), request), RejectionCodes.FeatureDisabled);

        s.Advance(TimeSpan.FromHours(49));
        s.Act(new FireTimers());

        Assert.Empty(s.LastEvents<PartnerRequestClosed>());
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);
    }

    [Fact]
    public void Switched_back_on_a_request_past_its_term_closes_by_the_first_timer()
    {
        // D-420: the mechanic continues from where it stopped — what is overdue goes at the first timer
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = false } });
        s.Advance(TimeSpan.FromHours(49));
        s.Act(new FireTimers());
        s.WithRuleset(r => r with { Features = r.Features with { PartnerBoard = true } });

        s.Act(new FireTimers());

        ScenarioAssert.Accepted(s);
        var closed = Assert.Single(s.LastEvents<PartnerRequestClosed>());
        Assert.Equal((request, PartnerRequestCloseReason.Timeout), (closed.RequestId, closed.Reason));
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
}
