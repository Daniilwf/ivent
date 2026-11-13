using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.GlobalEvents.GlobalSetup;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// Findings of the review of task 5.9 (rules-auditor, code-reviewer): undos that would leave a state the engine itself never
/// allows (D-750, D-751), the global event's vote at its end and with polls off (D-753), the end past the term, the deadline
/// and the term of what the event gave.
/// </summary>
public class GlobalEventReviewTests
{
    private static ObjectDefinition VoteGenre() => Event("vote-genre", new EffectSpec
    {
        Target = All,
        Actions =
        [
            new RequestChoiceAction
            {
                Prompt = "Какой жанр всем?",
                Options = new ChoiceOptionsSpec { From = ChoiceSource.Categories },
                Vote = new PollVoteSpec { Voters = PollVoters.Players, Anonymous = false, ClosesInHours = 24 },
            },
            new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$choice")]) },
        ],
    });

    private static Scenario VoteSeason() => Season(Rules(polls: true), pack: Pack([VoteGenre()]));

    private static Guid Ended(Scenario s, Guid id)
    {
        s.Act(new EndGlobalEvent(id));
        ScenarioAssert.Accepted(s);
        return s.LastCommandId;
    }

    // ---- Undo (D-750, D-751) ----

    [Fact]
    public void Undoing_an_end_after_a_later_launch_at_the_limit_is_refused()
    {
        var s = Season(Rules(maxActive: 1));
        var first = s.Launch("coin-rain", 72);
        s.SettleAll();
        var end = Ended(s, first.GlobalEventId);
        s.Launch("bonus-day", 72);
        s.SettleAll();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(end, "Ошибка админа")), RejectionCodes.GlobalEventTooMany);
    }

    [Fact]
    public void Undoing_an_end_below_the_limit_is_accepted()
    {
        var s = Season(Rules(maxActive: 2));
        var first = s.Launch("coin-rain", 72);
        s.SettleAll();
        var end = Ended(s, first.GlobalEventId);
        s.Launch("bonus-day", 72);
        s.SettleAll();

        s.Act(new UndoCommand(end, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(GlobalEventStatus.Active, s.Global(first.GlobalEventId).Status);
    }

    [Fact]
    public void Undoing_the_end_after_a_completion_in_its_window_is_refused_and_names_the_completion()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, RunsGoal(5));
        var end = Ended(s, launched.GlobalEventId);
        s.Completed("Вася", "Silent Hill");
        Assert.Empty(s.LastEvents<GlobalProgressChanged>());
        var completion = s.LastCommandId;

        s.Act(new UndoCommand(end, "Ошибка админа"));

        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Contains(completion, s.Last.Rejection.Related!);
    }

    [Fact]
    public void Undoing_the_end_after_the_mechanic_was_switched_off_is_refused()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72);
        s.SettleAll();
        var end = Ended(s, launched.GlobalEventId);
        s.Act(new ChangeRuleset(s.State.Rules with { Features = s.State.Rules.Features with { GlobalEvents = false } }));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(end, "Ошибка админа")), RejectionCodes.GlobalEventsInPlay);
    }

    [Fact]
    public void An_end_with_the_mechanic_off_is_refused()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72);
        s.SettleAll();
        Ended(s, launched.GlobalEventId);
        s.Act(new ChangeRuleset(s.State.Rules with { Features = s.State.Rules.Features with { GlobalEvents = false } }));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new EndGlobalEvent(launched.GlobalEventId)), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void Undoing_the_deadline_brings_the_ended_event_back()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, RunsGoal(5));
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        Assert.Equal(GlobalEventEndReason.Deadline, s.Global(launched.GlobalEventId).EndReason);

        s.Act(new UndoCommand(s.LastCommandId, "Рано закрыли"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(GlobalEventStatus.Active, s.Global(launched.GlobalEventId).Status);
        Assert.Null(s.Global(launched.GlobalEventId).EndReason);
    }

    // ---- The end ----

    [Fact]
    public void The_admins_end_past_the_term_is_written_as_the_timers()
    {
        var s = Season();
        var launched = s.Launch("banner", 6);
        s.Advance(TimeSpan.FromHours(7));

        s.Act(new EndGlobalEvent(launched.GlobalEventId));

        Assert.Equal(GlobalEventEndReason.Timer, Assert.Single(s.LastEvents<GlobalEventEnded>()).Reason);
    }

    [Fact]
    public void What_the_event_gave_lives_its_own_term_after_the_event_ends()
    {
        var s = Season();
        var launched = s.Launch("shield-day", 72);
        s.SettleAll();
        Ended(s, launched.GlobalEventId);
        Assert.All(Names, p => Assert.Equal(1, s.Count(p, "day-shield")));

        s.Advance(TimeSpan.FromHours(24)).Act(new FireTimers());

        Assert.All(Names, p => Assert.Equal(0, s.Count(p, "day-shield")));
    }

    [Fact]
    public void A_correction_after_the_deadline_does_not_reach_the_goal()
    {
        var s = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(30)));
        var launched = s.Launch("banner", 72, HoursGoal(12));
        var run = s.Completed("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromHours(30));

        s.Act(new CorrectRunHours(run, 14, "По пруфу"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(14m, Assert.Single(s.LastEvents<GlobalProgressChanged>()).Total);
        Assert.Empty(s.LastEvents<GlobalGoalReached>());
        Assert.False(s.Global(launched.GlobalEventId).Reached);
    }

    // ---- The vote (D-753) ----

    [Fact]
    public void The_timer_end_cancels_the_open_vote()
    {
        var s = VoteSeason();
        s.Launch("vote-genre", 30);
        var poll = Assert.Single(s.LastEvents<PollStarted>());
        s.Act(new ClosePoll(poll.PollId));
        s.Act(new UndoCommand(s.LastCommandId, "Рано закрыли"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(PollStatus.Open, s.State.Polls[poll.PollId].Status);

        // The poll's own close is due too, but the polls close first: with no votes it closes without a result
        s.Advance(TimeSpan.FromHours(30)).Act(new FireTimers());

        Assert.Single(s.LastEvents<GlobalEventEnded>());
        Assert.Empty(s.LastEvents<ForAllQueued>());
    }

    [Fact]
    public void One_late_timer_past_the_vote_and_the_end_settles_the_result_and_then_ends_the_event()
    {
        var s = VoteSeason();
        var launched = s.Launch("vote-genre", 30);
        var poll = Assert.Single(s.LastEvents<PollStarted>());
        s.Act(new CastVote(poll.PollId, s.Player("Вася").UserId, "Horror"));
        ScenarioAssert.Accepted(s);

        s.Advance(TimeSpan.FromHours(40)).Act(new FireTimers());

        var events = s.Last.Events;
        var closed = events.ToList().FindIndex(e => e is PollClosed);
        var queued = events.ToList().FindIndex(e => e is ForAllQueued);
        var ended = events.ToList().FindIndex(e => e is GlobalEventEnded);
        Assert.True(closed >= 0 && closed < queued && queued < ended, "The poll closes, its result is queued, then the event ends.");
        Assert.Empty(s.LastEvents<PollCancelled>());
        Assert.Equal(GlobalEventStatus.Ended, s.Global(launched.GlobalEventId).Status);
    }

    [Fact]
    public void Switching_polls_off_while_a_global_events_vote_is_open_is_refused()
    {
        var s = VoteSeason();
        s.Launch("vote-genre", 72);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ChangeRuleset(x.State.Rules with { Features = x.State.Rules.Features with { Polls = false } })), RejectionCodes.GlobalEventsInPlay);
    }

    [Fact]
    public void Undoing_a_launch_with_an_open_vote_removes_the_event_and_its_vote()
    {
        var s = VoteSeason();
        var launched = s.Launch("vote-genre", 72);
        var poll = Assert.Single(s.LastEvents<PollStarted>());

        s.Act(new UndoCommand(s.LastCommandId, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.False(s.State.GlobalEvents.ContainsKey(launched.GlobalEventId));
        Assert.False(s.State.Polls.ContainsKey(poll.PollId));
    }

    [Fact]
    public void Undoing_the_end_that_cancelled_the_vote_reopens_both()
    {
        var s = VoteSeason();
        var launched = s.Launch("vote-genre", 72);
        var poll = Assert.Single(s.LastEvents<PollStarted>());
        var end = Ended(s, launched.GlobalEventId);
        Assert.Equal(PollStatus.Cancelled, s.State.Polls[poll.PollId].Status);

        s.Act(new UndoCommand(end, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(GlobalEventStatus.Active, s.Global(launched.GlobalEventId).Status);
        Assert.Equal(PollStatus.Open, s.State.Polls[poll.PollId].Status);
    }

    [Fact]
    public void A_voted_game_answers_the_question_for_everyone()
    {
        // D-753 (by review): the answer is checked against the game options of the question, not the target's availability;
        // a voted game the target cannot roll is dropped with a record at his roll (D-721)
        var vote = Event("vote-game", new EffectSpec
        {
            Target = All,
            Actions =
            [
                new RequestChoiceAction
                {
                    Prompt = "Какую игру всем?",
                    Options = new ChoiceOptionsSpec { From = ChoiceSource.Games },
                    Vote = new PollVoteSpec { Voters = PollVoters.Players, Anonymous = false, ClosesInHours = 24 },
                },
                new ModifyNextRollAction { Filter = new GameFilterSpec { Games = ["$choice"] } },
            ],
        });
        var s = Season(Rules(polls: true), pack: Pack([vote]));
        s.Launch("vote-game", 72);
        var poll = Assert.Single(s.LastEvents<PollStarted>());
        s.Act(new CastVote(poll.PollId, s.Player("Петя").UserId, poll.Options[0].Id));
        s.Act(new ClosePoll(poll.PollId));
        Assert.Equal([poll.Options[0].Id], s.Queued().Answers!.Value);

        s.SettleAll();

        var settled = s.Log.OfType<ForAllSettled>().ToList();
        Assert.Equal(Names.Length, settled.Sum(b => b.Players.Count));
        Assert.All(settled, b => Assert.Empty(b.Skipped));
    }
}
