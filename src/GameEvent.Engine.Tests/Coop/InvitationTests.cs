using GameEvent.Engine.Economy;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// Invitations to a coop run (EV28, EV43; D-520, D-523, D-546 (8)): the roller of a running coop game invites a player; the
/// invited accepts or declines, the inviter withdraws; an invitation lives <c>interactions.inviteTtlHours</c> and burns by
/// the timer, by the end of its run and by the deadline; an accept checks everything again at its moment. One pending coop
/// invitation per run. Scenarios S5.10-01…-08, -23, -27 of the stage 5 plan.
/// </summary>
public class InvitationTests
{
    // ---- Invite and answer (S5.10-01, EV28) ----

    [Theory]
    [InlineData(24)]
    [InlineData(6)]
    public void Inviting_to_a_running_coop_run_creates_a_pending_invitation_for_the_ttl(int ttlHours)
    {
        var s = New(r => r.WithCoop(inviteTtlHours: ttlHours));
        var run = s.PlayTitle("Вася", CoopGame);
        var now = s.Clock.UtcNow;

        s.Act(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")));

        ScenarioAssert.Accepted(s);
        var created = Assert.IsType<InteractionCreated>(Assert.Single(s.Last.Events));
        var id = created.Interaction.InteractionId;
        var expected = new Interaction(id, InteractionKind.Coop, s.PlayerId("Вася"), s.PlayerId("Петя"), run, now, now.AddHours(ttlHours));
        Assert.Equal(expected, created.Interaction);
        Assert.Equal(InteractionStatus.Pending, created.Interaction.Status);
        Assert.Equal(expected, s.Invitation(id));

        // Inviting changes nobody's turn: the partner joins only on accepting
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
        Assert.Null(s.State.Runs[run].Coop);
    }

    [Fact]
    public void Accepting_joins_the_partner_to_the_run_as_playing_it()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(3));
        var now = s.Clock.UtcNow;

        s.Answer("Петя", id);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new IGameEvent[]
            {
                new InteractionAnswered(id, s.PlayerId("Петя"), Accepted: true, now),
                new CoopJoined(run, s.PlayerId("Петя"), s.PlayerId("Вася"), id, now),
            },
            s.Last.Events);
        Assert.Equal(InteractionStatus.Accepted, s.Invitation(id).Status);
        Assert.Equal(new CoopPartner(s.PlayerId("Петя"), id, now), s.State.Runs[run].Coop);
        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);

        // The run stays the roller's: one run, his snapshot; the partner's phase is «playing» that run
        Assert.Equal(s.PlayerId("Вася"), s.State.Runs[run].PlayerId);
        Assert.Equal((TurnPhase.Playing, (Guid?)run), (s.Player("Петя").Phase, s.Player("Петя").ActiveRunId));
        Assert.Equal((TurnPhase.Playing, (Guid?)run), (s.Player("Вася").Phase, s.Player("Вася").ActiveRunId));
    }

    [Fact]
    public void A_partner_in_a_coop_cannot_roll_a_run_of_his_own()
    {
        // D-540: the partner never has a run of his own beside the coop one — the coop run takes his active run
        var s = New();
        s.CoopRun();

        s.Rejects(new RollGame(s.PlayerId("Петя")), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Declining_leaves_both_as_they_were()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        var now = s.Clock.UtcNow;

        s.Answer("Петя", id, accept: false);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new InteractionAnswered(id, s.PlayerId("Петя"), Accepted: false, now), Assert.Single(s.Last.Events));
        Assert.Equal(InteractionStatus.Declined, s.Invitation(id).Status);
        Assert.Null(s.State.Runs[run].Coop);
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);

        // Answered once — answered: a later accept finds nothing pending
        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.InteractionNotPending);
    }

    // ---- Who answers and who withdraws (S5.10-03, EV28) ----

    [Fact]
    public void Only_the_invited_answers()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");

        s.Rejects(new AnswerInvitation(s.PlayerId("Маша"), id, Accept: true), RejectionCodes.InteractionNotYours);
        s.Rejects(new AnswerInvitation(s.PlayerId("Вася"), id, Accept: true), RejectionCodes.InteractionNotYours);
    }

    [Fact]
    public void Only_the_inviter_withdraws_and_a_withdrawn_invitation_cannot_be_accepted()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.Rejects(new WithdrawInvitation(s.PlayerId("Петя"), id), RejectionCodes.InteractionNotYours);
        var now = s.Clock.UtcNow;

        s.Act(new WithdrawInvitation(s.PlayerId("Вася"), id));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new InteractionWithdrawn(id, s.PlayerId("Вася"), now), Assert.Single(s.Last.Events));
        Assert.Equal(InteractionStatus.Withdrawn, s.Invitation(id).Status);
        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.InteractionNotPending);
        s.Rejects(new WithdrawInvitation(s.PlayerId("Вася"), id), RejectionCodes.InteractionNotPending);
        Assert.Null(s.State.Runs[run].Coop);
    }

    [Fact]
    public void An_unknown_invitation_is_refused()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var unknown = SequentialIds.Make(0x5A000000, 1);

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), unknown, Accept: true), RejectionCodes.InteractionUnknown);
        s.Rejects(new WithdrawInvitation(s.PlayerId("Вася"), unknown), RejectionCodes.InteractionUnknown);
    }

    // ---- The timer (S5.10-04, EV28, EV36) ----

    [Fact]
    public void An_invitation_burns_by_the_timer_after_its_ttl()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        s.Act(new FireTimers());

        ScenarioAssert.Accepted(s);
        var expired = Assert.Single(s.LastEvents<InteractionExpired>());
        Assert.Equal((id, InteractionExpiry.Timeout), (expired.InteractionId, expired.Reason));
        Assert.Equal(InteractionStatus.Expired, s.Invitation(id).Status);
        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.InteractionNotPending);
    }

    [Fact]
    public void Before_its_ttl_the_timer_leaves_the_invitation_and_an_accept_joins()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));

        // Nothing is due yet: refused as such or accepted without touching the invitation
        s.Act(new FireTimers());
        Assert.Empty(s.LastEvents<InteractionExpired>());
        Assert.Equal(InteractionStatus.Pending, s.Invitation(id).Status);

        s.Accept("Петя", id);
        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);
    }

    [Fact]
    public void An_accept_past_the_ttl_is_refused_even_before_the_timer_fired()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.InteractionNotPending);
    }

    // ---- What can be invited (S5.10-02, D-520) ----

    [Fact]
    public void Only_a_coop_game_takes_a_partner()
    {
        var s = New();
        s.PlayTitle("Вася", SoloGame);

        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")), RejectionCodes.CoopNotCoopGame);
    }

    [Fact]
    public void Without_a_running_run_of_his_own_nobody_invites()
    {
        var s = New();

        // Idle
        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")), RejectionCodes.CoopRunNotActive);

        // Offered the coop game, not started yet
        s.RollTitle("Вася", CoopGame);
        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")), RejectionCodes.CoopRunNotActive);
    }

    [Fact]
    public void The_partner_of_a_coop_run_does_not_invite_to_it()
    {
        var s = New();
        s.CoopRun();

        s.Rejects(new InviteToCoop(s.PlayerId("Петя"), s.PlayerId("Маша")), RejectionCodes.CoopRollerOnly);
    }

    [Fact]
    public void Self_an_unknown_or_an_inactive_player_is_no_partner()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        s.Act(new SetPlayerInactive(s.PlayerId("Коля"), IsInactive: true));
        ScenarioAssert.Accepted(s);

        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Вася")), RejectionCodes.CoopPartnerInvalid);
        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), SequentialIds.Make(0x5B000000, 1)), RejectionCodes.CoopPartnerInvalid);
        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Коля")), RejectionCodes.CoopPartnerInvalid);
    }

    [Fact]
    public void A_partner_marked_inactive_after_the_invitation_cannot_accept()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Коля");
        s.Act(new SetPlayerInactive(s.PlayerId("Коля"), IsInactive: true));
        ScenarioAssert.Accepted(s);

        s.Rejects(new AnswerInvitation(s.PlayerId("Коля"), id, Accept: true), RejectionCodes.CoopPartnerInvalid);
    }

    // ---- Limits (S5.10-05, S5.10-07, D-523) ----

    [Fact]
    public void One_pending_coop_invitation_per_run()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        s.Invite("Вася", "Петя");

        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Маша")), RejectionCodes.CoopInviteAlreadyOpen);
        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")), RejectionCodes.CoopInviteAlreadyOpen);
    }

    [Fact]
    public void After_a_withdrawal_another_player_is_invited_and_his_accept_leaves_nothing_pending_on_the_run()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var toPetya = s.Invite("Вася", "Петя");
        s.Act(new WithdrawInvitation(s.PlayerId("Вася"), toPetya));
        ScenarioAssert.Accepted(s);
        var toMasha = s.Invite("Вася", "Маша");

        s.Accept("Маша", toMasha);

        Assert.Equal(s.PlayerId("Маша"), s.State.Runs[run].PartnerId);
        Assert.DoesNotContain(s.State.Interactions.Values, i => i.RunId == run && i.Status == InteractionStatus.Pending);
        Assert.Equal(InteractionStatus.Withdrawn, s.Invitation(toPetya).Status);
    }

    [Fact]
    public void A_run_with_a_partner_takes_no_more_invitations()
    {
        var s = New();
        s.CoopRun();

        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Маша")), RejectionCodes.CoopAlreadyJoined);
    }

    // ---- The accept checks everything again (S5.10-06, EV43) ----

    [Fact]
    public void Inviting_a_busy_player_is_allowed_but_he_cannot_accept_while_busy()
    {
        var s = New();
        s.PlayTitle("Петя", SoloGame);
        s.PlayTitle("Вася", CoopGame);

        var id = s.Invite("Вася", "Петя");

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.CoopPartnerBusy);

        // Free again (his run completed), he joins
        s.CompleteWith("Петя", 1, 1);
        s.Accept("Петя", id);
    }

    [Fact]
    public void A_player_who_rolled_after_the_invitation_cannot_accept_with_the_offer_pending()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.RollTitle("Петя", SoloGame);

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.CoopPartnerBusy);
    }

    [Fact]
    public void A_decline_needs_no_free_partner()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.RollTitle("Петя", SoloGame);

        s.Answer("Петя", id, accept: false);

        ScenarioAssert.Accepted(s);
        Assert.Equal(InteractionStatus.Declined, s.Invitation(id).Status);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("drop")]
    [InlineData("techReroll")]
    public void The_end_of_the_run_burns_its_invitation_and_an_accept_after_is_refused(string end)
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");

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
        var expired = Assert.Single(s.LastEvents<InteractionExpired>());
        Assert.Equal((id, InteractionExpiry.RunEnded), (expired.InteractionId, expired.Reason));
        Assert.Equal(InteractionStatus.Expired, s.Invitation(id).Status);

        // S5.10-06 (b): the accept after the inviter's drop finds the invitation closed — zero events
        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.InteractionNotPending);
    }

    // ---- The partner's own exclusions (D-546 (8)) ----

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
    public void A_player_for_whom_the_game_is_excluded_is_not_invited(OwnExclusion how)
    {
        var s = New();
        Exclude(s, "Петя", how);
        s.PlayTitle("Вася", CoopGame);

        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")), RejectionCodes.CoopGameExcluded);
    }

    [Fact]
    public void A_game_excluded_for_the_partner_after_the_invitation_refuses_his_accept()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");

        // The game is Вася's now, so no command of Петя's excludes it; the exclusion is written as if it had come earlier
        s.AppendCraftedEvents(new GameExcluded(s.PlayerId("Петя"), s.GameId(CoopGame), ExclusionReason.AlreadyPlayed));

        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), id, Accept: true), RejectionCodes.CoopGameExcluded);
    }

    [Fact]
    public void A_game_dropped_by_another_player_does_not_stop_the_invitation()
    {
        // «Дропнутая другим — доступна»: Маша's drop excludes the game for her only
        var s = New();
        Exclude(s, "Маша", OwnExclusion.Dropped);
        var run = s.PlayTitle("Вася", CoopGame);

        s.Accept("Петя", s.Invite("Вася", "Петя"));

        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);
    }

    // ---- The deadline (S5.10-23, EV37) ----

    [Fact]
    public void Closing_the_season_burns_pending_invitations_but_keeps_a_running_coop()
    {
        var s = New();
        var coop = s.CoopRun();
        s.PlayTitle("Маша", SecondCoopGame);
        var pending = s.Invite("Маша", "Коля");

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.Accepted(s);
        var expired = Assert.Single(s.LastEvents<InteractionExpired>());
        Assert.Equal((pending, InteractionExpiry.Deadline), (expired.InteractionId, expired.Reason));
        Assert.Equal(InteractionStatus.Expired, s.Invitation(pending).Status);
        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[coop].PartnerId);
        Assert.Equal((TurnPhase.Playing, (Guid?)coop), (s.Player("Петя").Phase, s.Player("Петя").ActiveRunId));
    }

    [Fact]
    public void After_the_deadline_nobody_invites_answers_or_leaves()
    {
        var s = New();
        s.CoopRun();
        s.PlayTitle("Маша", SecondCoopGame);
        var burned = s.Invite("Маша", "Коля");
        s.MoveStatusTo(SeasonStatus.Closing);

        s.Rejects(new InviteToCoop(s.PlayerId("Маша"), s.PlayerId("Коля")), RejectionCodes.SeasonDeadlinePassed);
        s.Rejects(new LeaveCoop(s.PlayerId("Петя")), RejectionCodes.SeasonDeadlinePassed);
        s.Rejects(new AnswerInvitation(s.PlayerId("Коля"), burned, Accept: true), RejectionCodes.SeasonDeadlinePassed);
    }

    // ---- The flag (S5.10-27, EV35) ----

    [Fact]
    public void With_the_coop_flag_off_every_coop_command_is_refused()
    {
        var s = New(coop: false);
        s.PlayTitle("Вася", CoopGame);
        var any = SequentialIds.Make(0x5A000000, 1);

        s.Rejects(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")), RejectionCodes.FeatureDisabled);
        s.Rejects(new AnswerInvitation(s.PlayerId("Петя"), any, Accept: true), RejectionCodes.FeatureDisabled);
        s.Rejects(new WithdrawInvitation(s.PlayerId("Вася"), any), RejectionCodes.FeatureDisabled);
        s.Rejects(new LeaveCoop(s.PlayerId("Петя")), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void With_the_flag_switched_off_pending_invitations_burn_by_their_timer()
    {
        // D-715, decided by the owner 2026-11-14: with the flag off nobody starts a new coop, so a pending invitation cannot be
        // accepted and burns at its own time (was: it froze, Н-25)
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        s.WithRuleset(r => r with { Features = r.Features with { Coop = false } });
        s.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

        s.Act(new FireTimers());

        ScenarioAssert.Accepted(s);
        var expired = Assert.Single(s.LastEvents<InteractionExpired>());
        Assert.Equal((id, InteractionExpiry.Timeout), (expired.InteractionId, expired.Reason));
        Assert.Equal(InteractionStatus.Expired, s.Invitation(id).Status);
    }
}
