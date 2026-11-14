using GameEvent.Engine.Economy;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// Steps of the partner board in scenarios (task 5.11, D-524, D-545): a season with <c>features.partnerBoard</c> and coop on,
/// requests living <c>partnerBoard.requestTtlHours</c> (48) and invitations 24 hours.
/// </summary>
internal static class PartnerBoardSteps
{
    /// <summary>A running season with the partner board on; <paramref name="more"/> changes the ruleset after it.</summary>
    public static Scenario Board(Func<Ruleset, Ruleset>? more = null, int mapLength = 40, int requestTtlHours = 48) =>
        New(r => more is null ? r.WithPartnerBoard(requestTtlHours) : more(r.WithPartnerBoard(requestTtlHours)), mapLength);

    /// <summary>The player posts a request on his running run; must be accepted. Returns the request.</summary>
    public static Guid Post(this Scenario s, string player)
    {
        s.Act(new PostPartnerRequest(s.PlayerId(player)));
        ScenarioAssert.Accepted(s);
        return Assert.Single(s.LastEvents<PartnerRequestPosted>()).Request.RequestId;
    }

    /// <summary>The player responds to the request; must be accepted. Returns the invitation the response created.</summary>
    public static Guid Respond(this Scenario s, string player, Guid request)
    {
        s.Act(new RespondToPartnerRequest(s.PlayerId(player), request));
        ScenarioAssert.Accepted(s);
        return Assert.Single(s.LastEvents<InteractionCreated>()).Interaction.InteractionId;
    }

    /// <summary>The request as the season holds it now.</summary>
    public static PartnerRequest Request(this Scenario s, Guid request) => s.State.PartnerRequests[request];

    /// <summary>
    /// Why a pending response is closed together with its request (D-742, decided by the owner 2026-11-14), written as
    /// <c>requestClosed</c>.
    /// </summary>
    public static InteractionExpiry RequestClosed => InteractionExpiry.RequestClosed;

    /// <summary>
    /// What the board keeps true after every command: an open request names a running run of its author with no partner, at
    /// most one open request per run, and the log replays to the state.
    /// </summary>
    public static void AssertBoardHolds(Scenario s)
    {
        // Open by the clock too: a request past its term is closed even before the timer fires
        var now = s.Clock.UtcNow;
        var open = s.State.PartnerRequests.Values.Where(r => r.Status == PartnerRequestStatus.Open && r.ExpiresAt > now).ToList();
        foreach (var request in open)
        {
            var run = s.State.Runs[request.RunId];
            Assert.Equal(RunStatus.Playing, run.Status);
            Assert.Equal(request.PlayerId, run.PlayerId);
            Assert.Null(run.Coop);
        }

        Assert.Equal(open.Count, open.Select(r => r.RunId).Distinct().Count());
        Assert.Equal(SeasonEngine.Replay(s.Log), s.State);
    }
}

/// <summary>
/// The partner board (EV32, EV42; D-524, D-545, D-712, D-713, D-740, Н-26): the roller of a running coop game without a
/// partner posts a request; another player's response is a coop invitation from him to the author, so the author keeps the
/// roll and both sides agree; several responses may wait on one request, accepting one supersedes the others and closes the
/// request. A request closes by a coop, by its term, by its author, and takes its pending responses along (D-742). Scenarios S5.11-01 (posting), -02 (refused requests),
/// -03 (response and accept), -04 (refused responses), -05 (closing by the term and by the author, Н-26), -08 (the first
/// finisher) of the stage 5 plan; the run's end, the deadline, the flags and undo are in <see cref="PartnerBoardEndTests"/>.
/// </summary>
public class PartnerBoardTests
{
    // ---- Posting (S5.11-01) ----

    [Theory]
    [InlineData(48)]
    [InlineData(6)]
    public void Posting_on_a_running_coop_run_opens_a_request_for_the_ttl(int ttlHours)
    {
        var s = PartnerBoardSteps.Board(requestTtlHours: ttlHours);
        var run = s.PlayTitle("Вася", CoopGame);
        var now = s.Clock.UtcNow;

        s.Act(new PostPartnerRequest(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        var posted = Assert.IsType<PartnerRequestPosted>(Assert.Single(s.Last.Events));
        var expected = new PartnerRequest(posted.Request.RequestId, s.PlayerId("Вася"), run, now, now.AddHours(ttlHours));
        Assert.Equal(expected, posted.Request);
        Assert.Equal(PartnerRequestStatus.Open, posted.Request.Status);
        Assert.Equal(expected, s.Request(expected.RequestId));

        // A request invites nobody by itself: no invitation, no partner
        Assert.Empty(s.State.Interactions);
        Assert.Null(s.State.Runs[run].Coop);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    // ---- Refused requests (S5.11-02) ----

    [Fact]
    public void A_request_on_a_solo_game_is_refused()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", SoloGame);

        s.Rejects(new PostPartnerRequest(s.PlayerId("Вася")), RejectionCodes.CoopNotCoopGame);
    }

    [Fact]
    public void Without_a_running_run_nobody_posts()
    {
        var s = PartnerBoardSteps.Board();

        // Idle
        s.Rejects(new PostPartnerRequest(s.PlayerId("Вася")), RejectionCodes.CoopRunNotActive);

        // Offered the coop game, not started yet
        s.RollTitle("Вася", CoopGame);
        s.Rejects(new PostPartnerRequest(s.PlayerId("Вася")), RejectionCodes.CoopRunNotActive);
    }

    [Fact]
    public void A_second_open_request_on_the_same_run_is_refused()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        s.Post("Вася");

        s.Rejects(new PostPartnerRequest(s.PlayerId("Вася")), RejectionCodes.PartnerRequestAlreadyOpen);
    }

    [Fact]
    public void After_the_author_closed_his_request_he_may_post_again_on_the_same_run()
    {
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var first = s.Post("Вася");
        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), first));
        ScenarioAssert.Accepted(s);

        var second = s.Post("Вася");

        Assert.NotEqual(first, second);
        Assert.Equal((run, PartnerRequestStatus.Open), (s.Request(second).RunId, s.Request(second).Status));
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(first).Status);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void The_partner_of_a_coop_run_does_not_post_on_it()
    {
        var s = PartnerBoardSteps.Board();
        s.CoopRun();

        s.Rejects(new PostPartnerRequest(s.PlayerId("Петя")), RejectionCodes.CoopRollerOnly);
    }

    [Fact]
    public void A_run_that_already_has_a_partner_takes_no_request()
    {
        var s = PartnerBoardSteps.Board();
        s.CoopRun();

        s.Rejects(new PostPartnerRequest(s.PlayerId("Вася")), RejectionCodes.CoopAlreadyJoined);
    }

    // ---- A response is an invitation to the author (S5.11-03) ----

    [Theory]
    [InlineData(24)]
    [InlineData(6)]
    public void A_response_creates_a_coop_invitation_from_the_responder_to_the_author(int inviteTtlHours)
    {
        var s = PartnerBoardSteps.Board(r => r.WithCoop(inviteTtlHours: inviteTtlHours));
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(1));
        var now = s.Clock.UtcNow;

        s.Act(new RespondToPartnerRequest(s.PlayerId("Петя"), request));

        ScenarioAssert.Accepted(s);
        var created = Assert.IsType<InteractionCreated>(Assert.Single(s.Last.Events));
        var id = created.Interaction.InteractionId;
        var expected = new Interaction(
            id, InteractionKind.Coop, s.PlayerId("Петя"), s.PlayerId("Вася"), run, now, now.AddHours(inviteTtlHours));
        Assert.Equal(expected, created.Interaction);
        Assert.Equal(expected, s.Invitation(id));

        // Responding joins nobody: the request stays open, Петя stays idle until the author accepts
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
        Assert.Null(s.State.Runs[run].Coop);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void The_author_accepting_a_response_joins_the_responder_keeps_the_roll_and_closes_the_request()
    {
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        s.Advance(TimeSpan.FromHours(2));
        var now = s.Clock.UtcNow;
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));

        s.Answer("Вася", response);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new IGameEvent[]
            {
                new InteractionAnswered(response, vasya, Accepted: true, now),
                new CoopJoined(run, petya, vasya, response, now),
                new PartnerRequestClosed(request, PartnerRequestCloseReason.Coop, now),
            },
            s.Last.Events);

        // The roller stays the author: the run is Вася's, Петя is its partner
        Assert.Equal(vasya, s.State.Runs[run].PlayerId);
        Assert.Equal(petya, s.State.Runs[run].PartnerId);
        Assert.Equal((TurnPhase.Playing, (Guid?)run), (s.Player("Петя").Phase, s.Player("Петя").ActiveRunId));
        Assert.Equal((TurnPhase.Playing, (Guid?)run), (s.Player("Вася").Phase, s.Player("Вася").ActiveRunId));
        Assert.Equal(InteractionStatus.Accepted, s.Invitation(response).Status);
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);

        // A closed request takes no more responses
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Маша"), request), RejectionCodes.PartnerRequestNotOpen);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void Only_the_author_answers_a_response()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var response = s.Respond("Петя", s.Post("Вася"));

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), response, Accept: true), RejectionCodes.InteractionNotYours);
        s.Rejects(new AnswerInvitation(s.PlayerId("Маша"), response, Accept: true), RejectionCodes.InteractionNotYours);
    }

    [Fact]
    public void A_declined_response_leaves_the_request_open_and_the_responder_may_respond_again()
    {
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        var now = s.Clock.UtcNow;

        s.Answer("Вася", response, accept: false);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new InteractionAnswered(response, s.PlayerId("Вася"), Accepted: false, now), Assert.Single(s.Last.Events));
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);
        Assert.Null(s.State.Runs[run].Coop);

        // Nothing of his is pending on the request any more
        var again = s.Respond("Петя", request);
        Assert.NotEqual(response, again);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void The_responder_withdraws_his_response_and_the_request_stays_open()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);

        s.Act(new WithdrawInvitation(s.PlayerId("Петя"), response));

        ScenarioAssert.Accepted(s);
        Assert.Equal(InteractionStatus.Withdrawn, s.Invitation(response).Status);
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);
        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), response, Accept: true), RejectionCodes.InteractionNotPending);
    }

    // ---- Several responses on one request (D-545) ----

    [Fact]
    public void Several_players_respond_to_one_request_and_accepting_one_supersedes_the_others()
    {
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var fromPetya = s.Respond("Петя", request);
        var fromMasha = s.Respond("Маша", request);
        var fromKolya = s.Respond("Коля", request);
        Assert.All(new[] { fromPetya, fromMasha, fromKolya }, id => Assert.Equal(InteractionStatus.Pending, s.Invitation(id).Status));
        var now = s.Clock.UtcNow;
        var (vasya, masha) = (s.PlayerId("Вася"), s.PlayerId("Маша"));

        s.Answer("Вася", fromMasha);

        ScenarioAssert.Accepted(s);
        var events = s.Last.Events.ToList();
        Assert.Equal(5, events.Count);
        Assert.Equal(new InteractionAnswered(fromMasha, vasya, Accepted: true, now), events[0]);
        Assert.Equal(new CoopJoined(run, masha, vasya, fromMasha, now), events[1]);
        Assert.Equal(
            new[] { fromPetya, fromKolya }.Order(),
            events.Skip(2).Take(2).Select(e => Assert.IsType<InteractionExpired>(e)).Select(e => e.InteractionId).Order());
        Assert.All(
            events.Skip(2).Take(2).Cast<InteractionExpired>(),
            e => Assert.Equal((InteractionExpiry.Superseded, now), (e.Reason, e.ExpiredAt)));
        Assert.Equal(new PartnerRequestClosed(request, PartnerRequestCloseReason.Coop, now), events[4]);

        Assert.Equal(masha, s.State.Runs[run].PartnerId);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(fromPetya).Status);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(fromKolya).Status);
        s.Rejects(new AnswerInvitation(vasya, fromPetya, Accept: true), RejectionCodes.InteractionNotPending);
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void One_responder_has_one_pending_response_on_a_request()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Respond("Петя", request);

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.PartnerRequestAlreadyResponded);
    }

    // ---- Refused responses (S5.11-04) ----

    [Fact]
    public void The_author_does_not_respond_to_his_own_request()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Вася"), request), RejectionCodes.PartnerRequestOwn);
    }

    [Fact]
    public void A_player_with_a_run_of_his_own_does_not_respond()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Маша", SoloGame);
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Маша"), request), RejectionCodes.CoopPartnerBusy);
    }

    [Fact]
    public void A_player_with_an_offered_game_does_not_respond()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.RollTitle("Маша", SoloGame);

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Маша"), request), RejectionCodes.CoopPartnerBusy);
    }

    [Fact]
    public void An_unknown_request_is_refused()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var unknown = SequentialIds.Make(0x5C000000, 1);

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), unknown), RejectionCodes.PartnerRequestUnknown);
        s.Rejects(new ClosePartnerRequest(s.PlayerId("Вася"), unknown), RejectionCodes.PartnerRequestUnknown);
    }

    [Fact]
    public void An_inactive_or_unknown_player_does_not_respond()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Act(new SetPlayerInactive(s.PlayerId("Коля"), IsInactive: true));
        ScenarioAssert.Accepted(s);

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Коля"), request), RejectionCodes.CoopPartnerInvalid);
        s.Rejects(new RespondToPartnerRequest(SequentialIds.Make(0x5B000000, 1), request), RejectionCodes.PlayerUnknown);
    }

    public enum OwnExclusion
    {
        Dropped,
        TechRerolled,
        AlreadyPlayed,
    }

    private static void Exclude(Scenario s, string player, OwnExclusion how)
    {
        switch (how)
        {
            case OwnExclusion.Dropped:
                s.PlayTitle(player, CoopGame);
                s.NextRandom(1, 1).Act(new DropRun(s.PlayerId(player)));
                break;
            case OwnExclusion.TechRerolled:
                s.PlayTitle(player, CoopGame);
                s.Act(new TechReroll(s.PlayerId(player), TechRerollReason.DoesNotLaunch, null));
                break;
            default:
                // «Уже проходил» is declared on the offered game, and rolls again at once
                s.RollTitle(player, CoopGame);
                s.Act(new DeclareAlreadyPlayed(s.PlayerId(player), s.GameId(CoopGame)));
                break;
        }

        ScenarioAssert.Accepted(s);
    }

    [Theory]
    [InlineData(OwnExclusion.Dropped)]
    [InlineData(OwnExclusion.TechRerolled)]
    [InlineData(OwnExclusion.AlreadyPlayed)]
    public void A_player_for_whom_the_game_is_excluded_does_not_respond(OwnExclusion how)
    {
        var s = PartnerBoardSteps.Board();
        Exclude(s, "Петя", how);
        if (s.Player("Петя").Phase != TurnPhase.Idle)
        {
            // The tech reroll and «уже проходил» offered him another game at once: he plays it out to be free again
            s.Start("Петя");
            s.CompleteWith("Петя", 1, 1);
        }

        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.CoopGameExcluded);
    }

    [Fact]
    public void A_game_dropped_by_another_player_does_not_stop_a_response()
    {
        var s = PartnerBoardSteps.Board();
        Exclude(s, "Маша", OwnExclusion.Dropped);
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");

        s.Accept("Вася", s.Respond("Петя", request));

        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);
    }

    [Fact]
    public void A_player_with_an_open_bet_on_the_run_does_not_respond()
    {
        var s = PartnerBoardSteps.Board(r => r with { Features = r.Features with { Bets = true } });
        s.CompleteTitle("Петя", SoloGame, 1, 1);
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Act(new PlaceBet(s.PlayerId("Петя"), s.PlayerId("Вася"), s.Ruleset.Bets.DeadlineOptionsDays[0], 1));
        ScenarioAssert.Accepted(s);

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.CoopPartnerHasBet);
    }

    [Fact]
    public void A_responder_at_the_limit_of_pending_invitations_does_not_respond()
    {
        var s = PartnerBoardSteps.Board(r => r.WithCoop(maxOpenInvites: 1));
        s.PlayTitle("Вася", CoopGame);
        var vasyasRequest = s.Post("Вася");
        s.PlayTitle("Маша", SecondCoopGame);
        var mashasRequest = s.Post("Маша");
        var first = s.Respond("Петя", vasyasRequest);

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), mashasRequest), RejectionCodes.InteractionTooMany);

        // Withdrawn, the response no longer holds the limit
        s.Act(new WithdrawInvitation(s.PlayerId("Петя"), first));
        ScenarioAssert.Accepted(s);
        s.Respond("Петя", mashasRequest);
    }

    // ---- The term (S5.11-05, EV36) ----

    [Fact]
    public void A_minute_before_its_term_the_request_takes_a_response_and_the_timer_leaves_it()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(48) - TimeSpan.FromMinutes(1));

        s.Act(new FireTimers());
        Assert.Empty(s.LastEvents<PartnerRequestClosed>());
        Assert.Equal(PartnerRequestStatus.Open, s.Request(request).Status);

        s.Respond("Петя", request);
    }

    [Fact]
    public void At_its_term_the_timer_closes_the_request()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(48));

        s.Act(new FireTimers());

        ScenarioAssert.Accepted(s);
        var closed = Assert.Single(s.LastEvents<PartnerRequestClosed>());
        Assert.Equal((request, PartnerRequestCloseReason.Timeout), (closed.RequestId, closed.Reason));
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.PartnerRequestNotOpen);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void Past_its_term_the_request_takes_no_response_even_before_the_timer_fired()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(48));

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.PartnerRequestNotOpen);

        s.Advance(TimeSpan.FromMinutes(1));
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.PartnerRequestNotOpen);
    }

    [Fact]
    public void Past_its_term_the_author_may_post_a_new_request_on_the_run()
    {
        // The expired request is closed by the clock already: it no longer holds «one open request per run»
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var old = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(49));

        var fresh = s.Post("Вася");

        Assert.NotEqual(old, fresh);
    }

    // ---- The author closes (S5.11-05) ----

    [Fact]
    public void The_author_closes_his_request()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(1));
        var now = s.Clock.UtcNow;

        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), request));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PartnerRequestClosed(request, PartnerRequestCloseReason.Author, now), Assert.Single(s.Last.Events));
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.PartnerRequestNotOpen);
        s.Rejects(new ClosePartnerRequest(s.PlayerId("Вася"), request), RejectionCodes.PartnerRequestNotOpen);
    }

    [Fact]
    public void Only_the_author_closes_a_request()
    {
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Respond("Петя", request);

        s.Rejects(new ClosePartnerRequest(s.PlayerId("Петя"), request), RejectionCodes.PartnerRequestNotAuthor);
        s.Rejects(new ClosePartnerRequest(s.PlayerId("Маша"), request), RejectionCodes.PartnerRequestNotAuthor);
    }

    // ---- Responses are cancelled with their request (Н-26, D-742) ----

    [Fact]
    public void Closing_the_request_by_its_author_cancels_its_pending_responses()
    {
        // D-742, decided by the owner 2026-11-14 (was: the response lived to its own term and could still be accepted)
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var fromPetya = s.Respond("Петя", request);
        var fromMasha = s.Respond("Маша", request);
        var now = s.Clock.UtcNow;

        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), request));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PartnerRequestClosed(request, PartnerRequestCloseReason.Author, now), Assert.Single(s.LastEvents<PartnerRequestClosed>()));
        Assert.Equal(
            new[] { new InteractionExpired(fromPetya, PartnerBoardSteps.RequestClosed, now), new InteractionExpired(fromMasha, PartnerBoardSteps.RequestClosed, now) }
                .OrderBy(e => e.InteractionId),
            s.LastEvents<InteractionExpired>().OrderBy(e => e.InteractionId));
        Assert.Equal(InteractionStatus.Expired, s.Invitation(fromPetya).Status);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(fromMasha).Status);

        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), fromPetya, Accept: true), RejectionCodes.InteractionNotPending);
        Assert.Null(s.State.Runs[run].Coop);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void A_request_timing_out_cancels_its_pending_responses()
    {
        // D-742: the response made at hour 47 would live 24 hours; the request ends at hour 48 and takes it along
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(47));
        var response = s.Respond("Петя", request);
        s.Advance(TimeSpan.FromHours(1));

        s.Act(new FireTimers());

        ScenarioAssert.Accepted(s);
        Assert.Equal(PartnerRequestCloseReason.Timeout, Assert.Single(s.LastEvents<PartnerRequestClosed>()).Reason);
        var expired = Assert.Single(s.LastEvents<InteractionExpired>(), e => e.InteractionId == response);
        Assert.Equal(PartnerBoardSteps.RequestClosed, expired.Reason);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(response).Status);

        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), response, Accept: true), RejectionCodes.InteractionNotPending);
        Assert.Null(s.State.Runs[run].Coop);
    }

    [Fact]
    public void Past_the_requests_term_its_response_is_not_accepted_even_before_the_timer_fired()
    {
        // D-741 (2): a request past its term is closed by the clock already; D-742: its responses close with it
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Advance(TimeSpan.FromHours(47));
        var response = s.Respond("Петя", request);
        s.Advance(TimeSpan.FromHours(1));

        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), response, Accept: true), RejectionCodes.InteractionNotPending);

        Assert.Null(s.State.Runs[run].Coop);
    }

    [Fact]
    public void A_response_whose_request_is_over_by_the_clock_holds_no_slot_of_the_invitations()
    {
        // D-742 with the limit (D-523): the response is no longer pending once its request's term ran out, even before the
        // timer fired — so it does not keep the responder's only slot until its own term
        var s = PartnerBoardSteps.Board(r => r.WithCoop(maxOpenInvites: 1), requestTtlHours: 6);
        s.PlayTitle("Вася", CoopGame);
        var vasyas = s.Post("Вася");
        var outlived = s.Respond("Петя", vasyas);
        s.PlayTitle("Маша", SecondCoopGame);
        var mashas = s.Post("Маша");
        s.Advance(TimeSpan.FromHours(4));
        Assert.Equal(InteractionStatus.Pending, s.Invitation(outlived).Status);

        s.Act(new RespondToPartnerRequest(s.PlayerId("Петя"), mashas));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<InteractionCreated>());
    }

    [Fact]
    public void A_response_whose_request_is_still_open_holds_its_slot()
    {
        // The boundary of the test above: one minute before the request's term the response still waits
        var s = PartnerBoardSteps.Board(r => r.WithCoop(maxOpenInvites: 1), requestTtlHours: 6);
        s.PlayTitle("Вася", CoopGame);
        var vasyas = s.Post("Вася");
        s.Respond("Петя", vasyas);
        s.PlayTitle("Маша", SecondCoopGame);
        var mashas = s.Post("Маша");
        s.Advance(TimeSpan.FromHours(4) - TimeSpan.FromMinutes(1));

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Петя"), mashas), RejectionCodes.InteractionTooMany);
    }

    [Fact]
    public void A_response_cancelled_with_its_request_does_not_burn_again_at_its_own_term()
    {
        // Was «a response outliving its request burns by its own term»: under D-742 no response outlives its request
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), request));
        ScenarioAssert.Accepted(s);
        Assert.Contains(s.LastEvents<InteractionExpired>(), e => e.InteractionId == response);
        s.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        // Nothing is left for the timer: its own term burns nothing a second time
        s.Rejects(new FireTimers(), RejectionCodes.TimersNothingDue);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(response).Status);
    }

    [Fact]
    public void Closing_a_request_leaves_answered_responses_direct_invitations_and_other_requests_alone()
    {
        // The boundary of D-742: only the still pending responses of that request are cancelled
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var declined = s.Respond("Петя", request);
        s.Answer("Вася", declined, accept: false);
        ScenarioAssert.Accepted(s);
        var pending = s.Respond("Маша", request);
        var direct = s.Invite("Вася", "Коля");
        s.PlayTitle("Петя", SecondCoopGame);
        var other = s.Post("Петя");
        var toOther = s.Respond("Коля", other);
        var now = s.Clock.UtcNow;

        s.Act(new ClosePartnerRequest(s.PlayerId("Вася"), request));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new InteractionExpired(pending, PartnerBoardSteps.RequestClosed, now), Assert.Single(s.LastEvents<InteractionExpired>()));
        Assert.Equal(InteractionStatus.Declined, s.Invitation(declined).Status);
        Assert.Equal(InteractionStatus.Pending, s.Invitation(direct).Status);
        Assert.Equal(InteractionStatus.Pending, s.Invitation(toOther).Status);
        Assert.Equal(PartnerRequestStatus.Open, s.Request(other).Status);
    }

    [Fact]
    public void Accepting_a_response_checks_the_responder_again()
    {
        // D-712 holds for responses: Петя rolled a game of his own after responding — the accept is refused (the request
        // stays open here: closing it would cancel the response first, D-742)
        var s = PartnerBoardSteps.Board();
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);
        s.RollTitle("Петя", SoloGame);

        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), response, Accept: true), RejectionCodes.CoopPartnerBusy);
    }

    // ---- A direct invitation beside the board (D-740) ----

    [Fact]
    public void The_author_may_invite_directly_while_responses_wait_and_that_coop_closes_the_request()
    {
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Петя", request);

        var direct = s.Invite("Вася", "Маша");

        // His own invitations still count one per run
        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Коля")), RejectionCodes.CoopInviteAlreadyOpen);
        var now = s.Clock.UtcNow;
        var (vasya, masha) = (s.PlayerId("Вася"), s.PlayerId("Маша"));

        s.Answer("Маша", direct);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new IGameEvent[]
            {
                new InteractionAnswered(direct, masha, Accepted: true, now),
                new CoopJoined(run, masha, vasya, direct, now),
                new InteractionExpired(response, InteractionExpiry.Superseded, now),
                new PartnerRequestClosed(request, PartnerRequestCloseReason.Coop, now),
            },
            s.Last.Events);
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(request).Status);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    [Fact]
    public void After_the_board_partner_left_the_author_may_post_again_and_the_same_player_may_respond()
    {
        var s = PartnerBoardSteps.Board();
        var run = s.PlayTitle("Вася", CoopGame);
        var first = s.Post("Вася");
        s.Accept("Вася", s.Respond("Петя", first));
        s.Act(new LeaveCoop(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);

        var second = s.Post("Вася");

        Assert.NotEqual(first, second);
        Assert.Equal(run, s.Request(second).RunId);
        Assert.Equal(PartnerRequestStatus.Closed, s.Request(first).Status);
        s.Respond("Петя", second);
        PartnerBoardSteps.AssertBoardHolds(s);
    }

    // ---- The first finisher (S5.11-08, EV42, D-713) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_first_finisher_does_not_post(bool frozen)
    {
        var s = PartnerBoardSteps.Board(mapLength: 4);
        var mashasRun = s.CompleteTitle("Маша", SoloGame, 3, 1);
        if (frozen)
        {
            s.Act(new ApproveProof(mashasRun, null, Comment));
            Assert.True(s.Player("Маша").Finish!.Frozen);
        }

        s.PlayTitle("Маша", CoopGame);

        s.Rejects(new PostPartnerRequest(s.PlayerId("Маша")), RejectionCodes.CoopFirstFinisher);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_first_finisher_does_not_respond(bool frozen)
    {
        var s = PartnerBoardSteps.Board(mapLength: 4);
        var mashasRun = s.CompleteTitle("Маша", SoloGame, 3, 1);
        if (frozen)
        {
            s.Act(new ApproveProof(mashasRun, null, Comment));
            Assert.True(s.Player("Маша").Finish!.Frozen);
        }

        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");

        s.Rejects(new RespondToPartnerRequest(s.PlayerId("Маша"), request), RejectionCodes.CoopFirstFinisher);
    }

    [Fact]
    public void A_responder_who_finished_first_after_responding_is_not_accepted()
    {
        var s = PartnerBoardSteps.Board(mapLength: 4);
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        var response = s.Respond("Маша", request);
        s.CompleteTitle("Маша", SoloGame, 3, 1);

        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), response, Accept: true), RejectionCodes.CoopFirstFinisher);
    }

    // ---- Replay ----

    private static (Scenario S, int Mark) PlayTheBoard()
    {
        var s = PartnerBoardSteps.Board();
        var mark = s.Log.Count;
        s.PlayTitle("Вася", CoopGame);
        var request = s.Post("Вася");
        s.Respond("Петя", request);
        var fromMasha = s.Respond("Маша", request);
        s.Accept("Вася", fromMasha);
        s.PlayTitle("Коля", SecondCoopGame);
        s.Post("Коля");
        s.Advance(TimeSpan.FromHours(49));
        s.Act(new FireTimers());
        ScenarioAssert.Accepted(s);
        return (s, mark);
    }

    [Fact]
    public void The_same_board_commands_write_the_same_log_and_the_log_replays_to_the_state()
    {
        var (a, markA) = PlayTheBoard();
        var (b, markB) = PlayTheBoard();

        Assert.Equal(a.Log.Skip(markA).ToList(), b.Log.Skip(markB).ToList());
        Assert.Contains(a.Log, e => e is PartnerRequestClosed { Reason: PartnerRequestCloseReason.Timeout });
        Assert.Equal(SeasonEngine.Replay(a.Log), a.State);
    }
}
