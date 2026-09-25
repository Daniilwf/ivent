using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Lifecycle.LifecycleSetup;

namespace GameEvent.Engine.Tests.Lifecycle;

/// <summary>
/// Closing the season at the deadline (SE1, SE2, SE3; SPEC «Статусы сезона», «После дедлайна броски запрещены, пруфы
/// принимаются»; K-7; D-101). The scheduler sends <see cref="ReachDeadline"/>: accepted only in an Active season and
/// only once the deadline has come by the engine's clock — <c>SeasonStatusChanged(Active → Closing)</c>; before it or
/// without a deadline <c>season.deadlineNotReached</c>, in any other status <c>season.notActive</c>. In Closing the
/// players' turns stay closed; proofs, approvals, rejects, reviews, the admin's corrections, a tech reroll turned into a
/// drop and player adjustments are allowed; the deadline cannot be changed any more (<c>season.closed</c>).
/// </summary>
public class ClosingTests
{
    // ---- ReachDeadline ----

    [Fact]
    public void Reaching_the_deadline_closes_the_season()
    {
        var s = New();
        ToDeadline(s);

        s.Act(new ReachDeadline());

        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonStatusChanged(SeasonStatus.Active, SeasonStatus.Closing)], s.Last.Events);
        Assert.Equal(SeasonStatus.Closing, s.State.Status);
    }

    [Fact]
    public void Reaching_the_deadline_late_closes_the_season_too()
    {
        // The scheduler was down for a day
        var s = New();
        ToDeadline(s);
        s.Advance(TimeSpan.FromDays(1));

        s.Act(new ReachDeadline());

        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonStatusChanged(SeasonStatus.Active, SeasonStatus.Closing)], s.Last.Events);
    }

    [Fact]
    public void Deadline_not_reached_yet_is_refused()
    {
        var s = New();
        JustBeforeDeadline(s);

        Refused(s, new ReachDeadline(), RejectionCodes.SeasonDeadlineNotReached);
    }

    [Fact]
    public void Season_without_a_deadline_is_refused()
    {
        var s = New();
        s.Act(new SetSeasonDeadline(null));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromDays(30));

        Refused(s, new ReachDeadline(), RejectionCodes.SeasonDeadlineNotReached);
    }

    [Fact]
    public void Deadline_moved_later_before_the_tick_is_refused()
    {
        var s = New();
        s.Act(new SetSeasonDeadline(Deadline(s) + TimeSpan.FromDays(3)));
        ScenarioAssert.Accepted(s);
        s.Advance(UntilDeadline);

        Refused(s, new ReachDeadline(), RejectionCodes.SeasonDeadlineNotReached);
    }

    [Fact]
    public void Second_tick_after_closing_is_refused()
    {
        var s = New();
        Close(s);

        Refused(s, new ReachDeadline(), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Draft_season_past_its_deadline_is_not_closed_by_the_scheduler()
    {
        var s = Scenario.New().AsDraft().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(1)));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(2));

        Refused(s, new ReachDeadline(), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Admin_may_close_before_the_deadline()
    {
        var s = New();

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new SeasonStatusChanged(SeasonStatus.Active, SeasonStatus.Closing)], s.Last.Events);
    }

    [Fact]
    public void Reaching_the_deadline_changes_nothing_else()
    {
        var s = New();
        CompleteRun(s, "Вася", [2, 2]);
        s.Roll("Петя").Start("Петя");
        ToDeadline(s);
        var before = s.State;

        s.Act(new ReachDeadline());

        Assert.Equal(before with { Status = SeasonStatus.Closing }, s.State);
    }

    // ---- In Closing ----

    [Fact]
    public void Player_turns_stay_closed_in_closing()
    {
        var s = New();
        Close(s);
        var before = s.State;

        s.ExpectRejection().Roll("Вася");

        Assert.False(s.Last.IsAccepted);
        Assert.Contains(s.Last.Rejection!.Code, new[] { RejectionCodes.SeasonDeadlinePassed, RejectionCodes.SeasonNotActive });
        Assert.Equal(before, s.State);
    }

    [Fact]
    public void Proofs_approvals_and_rejects_are_allowed_in_closing()
    {
        var s = New();
        var vasyaRun = CompleteRun(s, "Вася", [2, 2]);
        var petyaRun = CompleteRun(s, "Петя", [1, 1]);
        Close(s);

        s.Act(new SubmitProof(s.PlayerId("Вася"), vasyaRun, ["https://imgur.com/a/credits"]));
        ScenarioAssert.Accepted(s);
        Approve(s, vasyaRun);
        ScenarioAssert.Accepted(s);
        Reject(s, petyaRun);
        ScenarioAssert.Accepted(s);

        Assert.Equal(RunStatus.Rejected, s.State.Runs[petyaRun].Status);
        Assert.Equal(0, s.Player("Петя").Points);
    }

    [Fact]
    public void Reviews_are_allowed_in_closing()
    {
        var s = New();
        var runId = CompleteRun(s, "Вася", [2, 2]);
        Close(s);

        s.Review("Вася", runId, 9, "Отличная игра");

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Admin_corrections_are_allowed_in_closing()
    {
        var s = New();
        var runId = CompleteRun(s, "Вася", [2, 2]);
        Close(s);

        s.NextRandom(3, 3).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));
        ScenarioAssert.Accepted(s);
        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "По пруфу — сложная"));
        ScenarioAssert.Accepted(s);
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Бонус за стрим", PointsDelta: 2));
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Tech_reroll_may_be_turned_into_a_drop_in_closing()
    {
        var s = New();
        s.Roll("Вася").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);
        Close(s);

        s.NextRandom(1, 1).Act(new ConvertTechRerollToDrop(runId, "это был дроп"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
    }

    [Fact]
    public void Deadline_cannot_be_moved_in_closing()
    {
        var s = New();
        Close(s);

        Refused(s, new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(7)), RejectionCodes.SeasonClosed);
        Refused(s, new SetSeasonDeadline(null), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Deadline_can_be_set_in_draft_and_active()
    {
        var s = Scenario.New().AsDraft().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(7)));
        ScenarioAssert.Accepted(s);

        s.Act(new ChangeSeasonStatus(SeasonStatus.Active));
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(8)));

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Offer_left_at_the_deadline_can_be_discarded_by_the_admin_in_closing()
    {
        // A player left Rolling cannot start any more; the admin clears the offer (D-89)
        var s = New();
        s.Roll("Вася");
        Close(s);

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сезон закрыт", DiscardOffer: true));

        ScenarioAssert.Accepted(s);
        Assert.Contains(s.Last.Events, e => e is OfferDiscarded);
    }
}
