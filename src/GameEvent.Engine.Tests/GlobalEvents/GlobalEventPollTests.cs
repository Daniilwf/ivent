using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.GlobalEvents.GlobalSetup;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// A vote for a global event (task 5.9, EV23; SPEC «Голосования» — «для ивентов вида „выберите игру лидеру“» and global
/// events, D-539, D-753): when the first action of the event's effect is <c>requestChoice</c> with <c>vote</c>, the launch
/// starts a poll of source <see cref="PollSource.GlobalEvent"/> instead of the settlement; its result answers the question
/// for everyone (<see cref="ForAllQueued.Answers"/>, <c>$choice</c>); no votes — no effect; the event's end and the deadline
/// cancel the open poll. Written by <c>test-author</c> from the SPEC and the public contract, before the implementation is
/// finished.
/// </summary>
public class GlobalEventPollTests
{
    private const string Prompt = "Какой жанр всем следующим?";

    /// <summary>«Жанр недели»: the players vote for a category, everyone gets «Навязанный жанр» of it.</summary>
    private static ObjectDefinition VoteGenre(int closesInHours = 24) => Event("vote-genre", new EffectSpec
    {
        Target = All,
        Actions =
        [
            new RequestChoiceAction
            {
                Prompt = Prompt,
                Options = new ChoiceOptionsSpec { From = ChoiceSource.Categories },
                Vote = new PollVoteSpec { Voters = PollVoters.Players, Anonymous = false, ClosesInHours = closesInHours },
            },
            new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$choice")]) },
        ],
    });

    /// <summary>Horror and RPG with games; Вася, Петя, Маша, Коля; polls on; «vote-genre» published.</summary>
    private static Scenario PollSeason(Ruleset? rules = null, int mapLength = 100, int closesInHours = 24)
    {
        var s = Scenario.New(rules ?? Rules(polls: true)).WithMapLength(mapLength)
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 10, "Horror").WithGame("Dead Space", 6, "Horror").WithGame("Outlast", 6, "Horror")
            .WithGame("Skyrim", 6, "RPG").WithGame("Persona", 6, "RPG")
            .WithPlayers(Names);
        return s.WithContent(Pack([VoteGenre(closesInHours)]));
    }

    private static PollStarted LaunchVote(Scenario s, int hours = 72)
    {
        s.Launch("vote-genre", hours);
        return Assert.Single(s.LastEvents<PollStarted>());
    }

    private static Scenario Voted(Scenario s, Guid pollId, string player, string option)
    {
        s.Act(new CastVote(pollId, s.Player(player).UserId, option));
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static Scenario CloseByTimer(Scenario s, Guid pollId)
    {
        s.Advance(s.State.Polls[pollId].ClosesAt - s.Clock.UtcNow);
        return s.Act(new FireTimers());
    }

    private static IReadOnlyList<string> Genres(Scenario s, string player) =>
        [.. s.Player(player).Wallet.Inventory.Where(o => o.ObjectId == "forced-genre").Select(o => o.Params!["tag"])];

    [Fact]
    public void The_launch_starts_the_vote_instead_of_the_settlement()
    {
        var s = PollSeason();

        s.Launch("vote-genre", 72);

        var launched = Assert.Single(s.LastEvents<GlobalEventLaunched>());
        var poll = Assert.Single(s.LastEvents<PollStarted>());
        Assert.Equal((PollSource.GlobalEvent, launched.GlobalEventId, "vote-genre"), (poll.Source, poll.GlobalEventId, poll.ObjectId));
        Assert.Equal(Prompt, poll.Question);
        Assert.Equal(["Horror", "RPG"], poll.Options.Select(o => o.Id).Order(StringComparer.Ordinal));
        Assert.Equal(s.Clock.UtcNow.AddHours(24), poll.ClosesAt);
        Assert.Empty(s.LastEvents<ForAllQueued>());
        Assert.Equal(poll.PollId, s.Global(launched.GlobalEventId).PollId);
    }

    [Fact]
    public void The_result_answers_the_question_for_everyone()
    {
        var s = PollSeason();
        var poll = LaunchVote(s);
        var globalEventId = poll.GlobalEventId!.Value;
        Voted(s, poll.PollId, "Вася", "RPG");
        Voted(s, poll.PollId, "Петя", "RPG");
        Voted(s, poll.PollId, "Маша", "Horror");

        CloseByTimer(s, poll.PollId);

        ScenarioAssert.Accepted(s);
        Assert.Equal("RPG", Assert.Single(s.LastEvents<PollClosed>()).Result);
        var queued = s.Queued();
        Assert.Equal((ForAllSource.GlobalEventLaunch, globalEventId), (queued.Source, queued.Reference));
        Assert.Equal(["RPG"], queued.Answers!.Value);
        Assert.Equal(s.State.Players.Keys, queued.Players);

        s.SettleAll();
        Assert.All(Names, p => Assert.Equal(["RPG"], Genres(s, p)));
        Assert.Equal(GlobalEventStatus.Active, s.Global(globalEventId).Status);
    }

    [Fact]
    public void The_admin_closing_the_vote_early_settles_its_result_too()
    {
        var s = PollSeason();
        var poll = LaunchVote(s);
        Voted(s, poll.PollId, "Коля", "Horror");

        s.Act(new ClosePoll(poll.PollId));

        ScenarioAssert.Accepted(s);
        Assert.Equal(["Horror"], s.Queued().Answers!.Value);
        s.SettleAll();
        Assert.All(Names, p => Assert.Equal(["Horror"], Genres(s, p)));
    }

    [Fact]
    public void A_vote_without_votes_gives_nothing()
    {
        var s = PollSeason();
        var poll = LaunchVote(s);

        CloseByTimer(s, poll.PollId);

        Assert.Null(Assert.Single(s.LastEvents<PollClosed>()).Result);
        Assert.Empty(s.LastEvents<ForAllQueued>());
        s.SettleAll();
        Assert.All(Names, p => Assert.Empty(Genres(s, p)));
    }

    [Fact]
    public void The_first_finisher_does_not_vote_and_gets_nothing()
    {
        var s = PollSeason(mapLength: 4);
        s.FinishWith("Маша", "Dead Space");
        var poll = LaunchVote(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new CastVote(poll.PollId, x.Player("Маша").UserId, "RPG")), RejectionCodes.PollFrozenVoter);

        Voted(s, poll.PollId, "Вася", "RPG");
        CloseByTimer(s, poll.PollId);
        Assert.DoesNotContain(s.PlayerId("Маша"), s.Queued().Players);
        s.SettleAll();
        Assert.Empty(Genres(s, "Маша"));
        Assert.Equal(["RPG"], Genres(s, "Вася"));
    }

    [Fact]
    public void Ending_the_event_cancels_its_open_vote()
    {
        var s = PollSeason();
        var poll = LaunchVote(s);
        var globalEventId = poll.GlobalEventId!.Value;
        Voted(s, poll.PollId, "Вася", "RPG");

        s.Act(new EndGlobalEvent(globalEventId));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PollCancelled(poll.PollId), Assert.Single(s.LastEvents<PollCancelled>()));
        Assert.Equal(new GlobalEventEnded(globalEventId, GlobalEventEndReason.Admin), Assert.Single(s.LastEvents<GlobalEventEnded>()));
        Assert.Equal(PollStatus.Cancelled, s.State.Polls[poll.PollId].Status);

        // Its time passes: nothing closes, nothing is settled
        CloseByTimer(s, poll.PollId);
        Assert.Empty(s.LastEvents<PollClosed>());
        Assert.Empty(s.LastEvents<ForAllQueued>());
        Assert.All(Names, p => Assert.Empty(Genres(s, p)));
    }

    [Fact]
    public void The_deadline_cancels_the_vote_and_ends_the_event()
    {
        // D-516: a poll with an effect is cancelled, the global event ends
        var s = PollSeason();
        var poll = LaunchVote(s);
        Voted(s, poll.PollId, "Вася", "RPG");

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PollCancelled(poll.PollId), Assert.Single(s.LastEvents<PollCancelled>()));
        Assert.Equal(GlobalEventEndReason.Deadline, Assert.Single(s.LastEvents<GlobalEventEnded>()).Reason);
        Assert.Empty(s.LastEvents<ForAllQueued>());
        s.Advance(TimeSpan.FromHours(24)).Act(new FireTimers());
        Assert.Empty(s.LastEvents<PollClosed>());
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(10, false)]
    [InlineData(25, true)]
    public void The_vote_closes_before_the_event_ends(int hours, bool valid)
    {
        var s = PollSeason();

        if (valid)
        {
            LaunchVote(s, hours);
        }
        else
        {
            ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("vote-genre", hours)), RejectionCodes.ContentInvalid);
        }
    }

    [Fact]
    public void With_polls_off_an_event_asking_a_vote_is_refused()
    {
        var s = PollSeason();
        s.WithRuleset(r => r with { Features = r.Features with { Polls = false } });

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("vote-genre", 72)), RejectionCodes.FeatureDisabled);

        // An event without a vote still launches
        s.Launch("coin-rain", 72);
    }

    [Fact]
    public void A_vote_whose_question_is_not_the_first_action_is_refused()
    {
        // D-753: only the first action may ask the voters
        var late = Event("late-vote", new EffectSpec
        {
            Target = All,
            Actions =
            [
                Coins(1),
                new RequestChoiceAction
                {
                    Prompt = Prompt,
                    Options = new ChoiceOptionsSpec { From = ChoiceSource.Categories },
                    Vote = new PollVoteSpec { Voters = PollVoters.Players, ClosesInHours = 24 },
                },
                new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$choice")]) },
            ],
        });
        var s = PollSeason();
        s.WithContent(Pack([VoteGenre(), late]));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new LaunchGlobalEvent("late-vote", 72)), RejectionCodes.ContentInvalid);
    }

    [Fact]
    public void The_log_with_a_vote_replays_to_the_same_state()
    {
        var s = PollSeason();
        var poll = LaunchVote(s);
        Voted(s, poll.PollId, "Вася", "RPG");
        CloseByTimer(s, poll.PollId);
        s.SettleAll();

        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }
}
