using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Polls.PollSetup;

namespace GameEvent.Engine.Tests.Polls;

/// <summary>
/// Polls and the deadline (task 5.8, EV37; SPEC «После дедлайна броски запрещены, пруфы принимаются»; D-516): the results
/// must not change after the deadline, so moving the season to «закрытие» cancels every open poll whose result acts
/// (<see cref="PollCancelled"/>, in the same command; an event's suspended effect ends without its actions), while a poll
/// without an effect (a nomination) waits for its time, takes votes and closes by the timer with its result. A new poll is
/// not started after the deadline; nothing is voted or closed in a finished season. Written by <c>test-author</c> from the
/// SPEC, before the implementation.
/// </summary>
public class PollDeadlineTests
{
    private sealed record PollIds(Guid Admin, Guid? Event, Guid Nomination);

    /// <summary>
    /// An admin poll with an effect, a nomination and, <paramref name="withEvent"/>, an event's poll, each with a vote. Without
    /// the event the season is <see cref="PollSetup.Season"/>, so the admin's polls are checked on their own.
    /// </summary>
    private static (Scenario S, PollIds P) Season(bool withEvent = false)
    {
        var s = withEvent ? EventPollTests.Season(EventPollTests.GenreVote()) : PollSetup.Season();
        var admin = s.Start(ContentPoll());
        s.Voted(admin.PollId, "Маша", "Horror");
        Guid? fromEvent = null;
        if (withEvent)
        {
            var started = EventPollTests.Draw(s);
            s.Voted(started.PollId, "Маша", started.Options[0].Id);
            fromEvent = started.PollId;
        }

        var nomination = s.Start(ListPoll());
        s.Voted(nomination.PollId, "Маша", "Финиш");
        s.Advance(TimeSpan.FromHours(1));
        return (s, new PollIds(admin.PollId, fromEvent, nomination.PollId));
    }

    private static void AssertCancelledAtTheDeadline(Scenario s, PollIds p)
    {
        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonStatus.Closing, s.State.Status);
        Assert.Equal(new[] { p.Admin, p.Event }.OfType<Guid>().Order(), s.LastEvents<PollCancelled>().Select(c => c.PollId).Order());
        Assert.Empty(s.LastEvents<PollClosed>());
        Assert.Empty(s.LastEvents<ObjectGiven>());
        Assert.Empty(s.LastEvents<EffectResumed>());
        Assert.Equal((PollStatus.Cancelled, PollStatus.Open), (s.State.Polls[p.Admin].Status, s.State.Polls[p.Nomination].Status));
        if (p.Event is { } fromEvent)
        {
            Assert.Equal(PollStatus.Cancelled, s.State.Polls[fromEvent].Status);
        }

        Assert.Null(s.State.Polls[p.Admin].Result);

        // The event's suspended effect is over without its actions: nothing waits for the poll any more
        Assert.Empty(s.Player("Вася").Suspended);
        Assert.Null(s.Player("Вася").Choice);
    }

    // ---- S5.8-20 (EV37) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_the_season_cancels_polls_with_an_effect_and_a_nomination_waits_for_its_time(bool withEvent)
    {
        var (s, p) = Season(withEvent);

        s.MoveStatusTo(SeasonStatus.Closing);

        AssertCancelledAtTheDeadline(s, p);

        // The nomination still takes votes until its time, the cancelled ones none
        s.Voted(p.Nomination, "Вася", "Финиш");
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(p.Admin, "Вася", "Horror"), RejectionCodes.PollClosed);
        if (p.Event is { } fromEvent)
        {
            ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(fromEvent, "Вася", x.State.Polls[fromEvent].Options[0].Id), RejectionCodes.PollClosed);
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ClosePoll(p.Admin)), RejectionCodes.PollClosed);

        s.CloseByTimer(p.Nomination);

        ScenarioAssert.Accepted(s);
        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Equal((p.Nomination, "Финиш"), (closed.PollId, closed.Result));
        Assert.Empty(s.LastEvents<PollCancelled>());
        Assert.Empty(s.Genres("Петя"));
        Assert.Empty(s.Genres("Вася"));
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reaching_the_deadline_by_the_scheduler_cancels_them_the_same_way(bool withEvent)
    {
        var (s, p) = Season(withEvent);
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(2)));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(2));

        s.Act(new ReachDeadline());

        AssertCancelledAtTheDeadline(s, p);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_poll_with_an_effect_closed_past_the_deadline_before_the_scheduler_closes_the_season_is_cancelled(bool withEvent)
    {
        // D-724: the deadline has come, ReachDeadline has not run yet; the close cancels instead of giving a result
        var (s, p) = Season(withEvent);
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(1)));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));

        s.Act(new ClosePoll(p.Admin));

        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonStatus.Active, s.State.Status);
        Assert.Equal(p.Admin, Assert.Single(s.LastEvents<PollCancelled>()).PollId);
        Assert.Empty(s.LastEvents<PollClosed>());
        Assert.Empty(s.LastEvents<ObjectGiven>());
        if (p.Event is { } fromEvent)
        {
            s.Act(new ClosePoll(fromEvent));
            ScenarioAssert.Accepted(s);
            Assert.Equal(fromEvent, Assert.Single(s.LastEvents<PollCancelled>()).PollId);
            Assert.Empty(s.LastEvents<EffectResumed>());
            Assert.Empty(s.Player("Вася").Suspended);
        }

        // A nomination still has its result after the deadline
        s.Act(new ClosePoll(p.Nomination));
        Assert.Equal("Финиш", Assert.Single(s.LastEvents<PollClosed>()).Result);
    }

    [Fact]
    public void Polls_switched_off_still_have_their_effect_polls_cancelled_at_the_deadline()
    {
        // D-724: the results must not change after the deadline, and an event's suspension must not wait for a frozen poll
        var (s, p) = Season(withEvent: true);
        s.Act(new Engine.Rulesets.ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { Polls = false } }));
        ScenarioAssert.Accepted(s);

        s.MoveStatusTo(SeasonStatus.Closing);

        Assert.Equal(new[] { p.Admin, p.Event!.Value }.Order(), s.LastEvents<PollCancelled>().Select(c => c.PollId).Order());
        Assert.Empty(s.Player("Вася").Suspended);
    }

    [Fact]
    public void Undoing_the_deadline_brings_back_the_cancelled_polls_and_the_events_question()
    {
        // D-722: the cancel is part of the command that closed the season
        var (s, p) = Season(withEvent: true);
        s.MoveStatusTo(SeasonStatus.Closing);
        var closing = s.LastCommandId;

        s.Act(new Engine.Undo.UndoCommand(closing, "Рано закрыли"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonStatus.Active, s.State.Status);
        Assert.Equal((PollStatus.Open, PollStatus.Open), (s.State.Polls[p.Admin].Status, s.State.Polls[p.Event!.Value].Status));
        Assert.Contains(s.Player("Вася").Suspended, x => x.PollId == p.Event);
    }

    [Fact]
    public void A_nomination_closed_by_the_admin_after_the_deadline_has_its_result()
    {
        var (s, p) = Season();
        s.MoveStatusTo(SeasonStatus.Closing);

        s.Act(new ClosePoll(p.Nomination));

        ScenarioAssert.Accepted(s);
        Assert.Equal("Финиш", Assert.Single(s.LastEvents<PollClosed>()).Result);
    }

    [Fact]
    public void No_poll_is_started_after_the_deadline()
    {
        var (s, _) = Season();
        s.MoveStatusTo(SeasonStatus.Closing);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ContentPoll())), RejectionCodes.SeasonDeadlinePassed);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ListPoll())), RejectionCodes.SeasonDeadlinePassed);
    }

    [Fact]
    public void The_timer_after_the_deadline_closes_nothing_cancelled()
    {
        var (s, p) = Season();
        s.MoveStatusTo(SeasonStatus.Closing);
        s.CloseByTimer(p.Nomination);

        Assert.DoesNotContain(s.LastEvents<PollClosed>(), c => c.PollId == p.Admin || c.PollId == p.Event);
        Assert.Equal(PollStatus.Cancelled, s.State.Polls[p.Admin].Status);

        // Again later: nothing more to close
        s.Advance(TimeSpan.FromDays(1)).Act(new FireTimers());
        Assert.Empty(s.LastEvents<PollClosed>());
        Assert.Empty(s.LastEvents<PollCancelled>());
    }

    [Fact]
    public void Nothing_is_voted_or_closed_in_a_finished_season()
    {
        var (s, p) = Season();
        s.MoveStatusToForcingFinish(SeasonStatus.Finished);
        Assert.Equal(SeasonStatus.Finished, s.State.Status);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(p.Nomination, "Вася", "Дроп"), RejectionCodes.SeasonClosed);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ClosePoll(p.Nomination)), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Before_the_deadline_nothing_is_cancelled()
    {
        // The boundary: a second before the deadline the polls go on as usual
        var (s, p) = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(2)));
        s.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));

        s.Act(new FireTimers());

        Assert.Empty(s.LastEvents<PollCancelled>());
        Assert.All(new[] { p.Admin, p.Nomination }, id => Assert.Equal(PollStatus.Open, s.State.Polls[id].Status));
        s.Voted(p.Admin, "Вася", "RPG");
    }
}
