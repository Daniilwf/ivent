using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Events;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Polls.PollSetup;

namespace GameEvent.Engine.Tests.Polls;

/// <summary>
/// Polls of the admin (task 5.8, SPEC «Голосования», EV21, EV22; D-512 the snapshot of the options, D-514 the result, D-515
/// and D-541 anonymity, D-519 no moves on everyone, D-537 the result on everyone as a settlement, D-544 an effect needs
/// items). Written by <c>test-author</c> from the SPEC and the public contract of <c>GameEvent.Engine.Polls</c>, before the
/// implementation: a poll starts with <see cref="StartPoll"/> and writes the snapshot of its question and options in
/// <see cref="PollStarted"/>, users vote with <see cref="CastVote"/>, the timer (<see cref="FireTimers"/>) or the admin
/// (<see cref="ClosePoll"/>) closes it with <see cref="PollClosed"/> and the effect of the result runs in the closing command.
/// </summary>
public class PollTests
{
    private static readonly EffectSpec s_gameToLeader = new()
    {
        Target = new TargetSpec { Selector = TargetSelector.Leader },
        Actions = [new ModifyNextRollAction { Filter = new GameFilterSpec { Games = ["$result"] } }],
    };

    // The sampling tests do not depend on filter.games: a result for the leader that only pays
    private static readonly EffectSpec s_coinsToLeader = new()
    {
        Target = new TargetSpec { Selector = TargetSelector.Leader },
        Actions = [Coins(1)],
    };

    // ---- S5.8-01 (EV21, EV22): the CONTENT.md example «жанр лидеру» is an acceptance test ----

    [Fact]
    public void The_content_example_gives_the_leader_the_voted_genre()
    {
        var s = Season();
        var definition = ContentPoll();
        var startedAt = s.Clock.UtcNow;

        var poll = s.Start(definition);

        // The snapshot: the question, the categories with an available game (Puzzle has only a deleted one), who votes, how
        Assert.Equal(PollSource.Admin, poll.Source);
        Assert.Equal("Какой жанр подкинем лидеру?", poll.Question);
        Assert.Equal(["Horror", "RPG", "Racing"], poll.Options.Select(o => o.Id).Order(StringComparer.Ordinal));
        Assert.All(poll.Options, o => Assert.Equal(o.Id, o.Label));
        Assert.Equal((PollVoters.Players, true, false), (poll.Voters, poll.Anonymous, poll.ShowLiveCounts));
        Assert.Equal((startedAt, startedAt.AddHours(24)), (poll.StartedAt, poll.ClosesAt));
        Assert.Equal(definition.OnResult, poll.OnResult);
        Assert.Null(poll.PlayerId);
        Assert.Equal(PollStatus.Open, s.State.Polls[poll.PollId].Status);

        s.Voted(poll.PollId, "Вася", "Horror");
        Assert.Equal(new VoteCast(poll.PollId, s.Player("Вася").UserId, "Horror", true, false), Assert.Single(s.Last.Events));
        s.Voted(poll.PollId, "Маша", "Horror").Voted(poll.PollId, "Коля", "RPG");
        Assert.Equal(3, s.State.Polls[poll.PollId].Votes.Count);

        s.CloseByTimer(poll.PollId);

        ScenarioAssert.Accepted(s);
        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Equal((poll.PollId, PollCloseReason.Timer, "Horror"), (closed.PollId, closed.Reason, closed.Result));
        Assert.Equal(poll.Options.Select(o => new PollCount(o.Id, o.Id switch { "Horror" => 2, "RPG" => 1, _ => 0 })), closed.Counts);
        Assert.Empty(closed.Tied);
        Assert.Null(closed.TieRoll);

        // The effect of the result runs in the closing command, after the close: Петя leads
        var given = Assert.Single(s.LastEvents<ObjectGiven>());
        Assert.Equal((s.PlayerId("Петя"), "forced-genre", "Horror"), (given.PlayerId, given.Item.ObjectId, given.Item.Params!["tag"]));
        Assert.True(s.IndexOf<PollClosed>() < s.IndexOf<ObjectGiven>());
        var state = s.State.Polls[poll.PollId];
        Assert.Equal((PollStatus.Closed, "Horror"), (state.Status, state.Result));

        // The special roll works as CONTENT.md says: Петя's next game is a horror
        s.Roll("Петя");
        Assert.Contains("Horror", s.PoolGame(s.Player("Петя").Offer!.GameId).Tags);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    // ---- S5.8-02 (EV22): the leader at the close, not at the start ----

    [Fact]
    public void The_result_goes_to_the_leader_at_the_close_not_at_the_start()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "RPG");

        // Маша overtakes Петя before the close
        s.WithPoints("Маша", 20);

        s.CloseByTimer(poll.PollId);

        Assert.Equal(s.PlayerId("Маша"), Assert.Single(s.LastEvents<ObjectGiven>()).PlayerId);
        Assert.Equal(["RPG"], s.Genres("Маша"));
        Assert.Empty(s.Genres("Петя"));
    }

    // ---- S5.8-03 (EV22, EV49): a tie is broken by the server's roll, written in the event ----

    [Theory]
    [InlineData(0, "Horror")]
    [InlineData(1, "RPG")]
    public void A_tie_is_broken_by_a_recorded_roll(int roll, string expected)
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror").Voted(poll.PollId, "Маша", "Horror");
        s.Voted(poll.PollId, "Петя", "RPG").Voted(poll.PollId, "Коля", "RPG");

        s.NextRandom(roll).CloseByTimer(poll.PollId);

        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Equal(["Horror", "RPG"], closed.Tied.Order(StringComparer.Ordinal));
        Assert.Equal(roll, closed.TieRoll);
        Assert.Equal(closed.Tied[roll], closed.Result);
        Assert.Equal(expected, closed.Result);
        Assert.Equal([expected], s.Genres("Петя"));
        Assert.Equal(0, s.Random.ScriptedLeft);

        // A replay of the log takes the recorded roll: no new roll, the same result
        var replayed = SeasonEngine.Replay(s.StoredLog);
        Assert.Equal(s.State, replayed);
        Assert.Equal(expected, replayed.Polls[poll.PollId].Result);
    }

    [Fact]
    public void A_single_option_with_the_most_votes_rolls_nothing()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");

        // A scripted value left for a roll that must not happen
        s.NextRandom(0).CloseByTimer(poll.PollId);

        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Equal(("Horror", 0, (int?)null), (closed.Result, closed.Tied.Count, closed.TieRoll));
        Assert.Equal(1, s.Random.ScriptedLeft);
    }

    // ---- S5.8-04 (EV22): no votes — no result, no effect ----

    [Fact]
    public void Without_votes_there_is_no_result_and_no_effect()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());

        s.CloseByTimer(poll.PollId);

        ScenarioAssert.Accepted(s);
        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Null(closed.Result);
        Assert.All(closed.Counts, c => Assert.Equal(0, c.Votes));
        Assert.Equal(poll.Options.Select(o => o.Id), closed.Counts.Select(c => c.OptionId));
        Assert.Empty(closed.Tied);
        Assert.Null(closed.TieRoll);
        Assert.Empty(s.LastEvents<ObjectGiven>());
        Assert.All(Names, p => Assert.Empty(s.Genres(p)));
        var state = s.State.Polls[poll.PollId];
        Assert.Equal((PollStatus.Closed, (string?)null), (state.Status, state.Result));
    }

    // ---- S5.8-12 (EV22): the admin closes early ----

    [Fact]
    public void The_admin_closes_a_poll_early_and_the_timer_then_does_nothing()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Racing");
        s.Advance(TimeSpan.FromHours(2));

        s.Act(new ClosePoll(poll.PollId));

        ScenarioAssert.Accepted(s);
        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Equal((PollCloseReason.Admin, "Racing"), (closed.Reason, closed.Result));
        Assert.Equal(["Racing"], s.Genres("Петя"));

        // Closed is closed: a vote, a second close, the timer at the poll's time
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Маша", "Horror"), RejectionCodes.PollClosed);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ClosePoll(poll.PollId)), RejectionCodes.PollClosed);
        s.Advance(TimeSpan.FromHours(23)).Act(new FireTimers());
        Assert.DoesNotContain(s.Last.Events, e => e is PollClosed or ObjectGiven);
        Assert.Equal(["Racing"], s.Genres("Петя"));
    }

    [Fact]
    public void An_unknown_poll_is_refused()
    {
        var s = Season();
        var unknown = Guid.Parse("5f000000-0000-0000-0000-000000000009");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ClosePoll(unknown)), RejectionCodes.PollUnknown);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(unknown, "Вася", "Horror"), RejectionCodes.PollUnknown);
    }

    // ---- S5.8-13 (EV21, EV49): games — a sample of polls.gameOptions of the games available to the target ----

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    public void A_poll_over_games_offers_only_games_available_to_the_leader(int gameOptions)
    {
        var s = Scenario.New(Rules(gameOptions: gameOptions)).WithCategory("Horror");
        for (var i = 1; i <= 8; i++)
        {
            s.WithGame($"Игра {i}", 6, "Horror");
        }

        s.WithDeletedGame("Удалённая", 6, "Horror").WithPlayers("Вася", "Петя", "Маша");

        // Петя drops his game (excluded for him), Маша completes hers (unavailable to all), Вася plays his (occupied)
        s.Roll("Петя").Start("Петя");
        var dropped = s.Player("Петя").Offer?.GameId ?? s.State.Runs[s.Player("Петя").ActiveRunId!.Value].GameId;
        s.Advance(TimeSpan.FromMinutes(61)).Act(new DropRun(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        s.Roll("Маша").Start("Маша");
        var completed = s.State.Runs[s.Player("Маша").ActiveRunId!.Value].GameId;
        s.Complete("Маша");
        s.Roll("Вася").Start("Вася");
        var occupied = s.State.Runs[s.Player("Вася").ActiveRunId!.Value].GameId;
        s.WithPoints("Петя", 50);
        var available = Enumerable.Range(1, 8).Select(i => s.GameId($"Игра {i}")).Except([dropped, completed, occupied]).Select(N).ToHashSet();

        var poll = s.Start(EffectPoll(s_coinsToLeader, ChoiceSource.Games));

        var ids = poll.Options.Select(o => o.Id).ToList();
        Assert.Equal(Math.Min(gameOptions, available.Count), ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Contains(id, available));
        Assert.All(poll.Options, o => Assert.Equal(s.GameTitle(Guid.ParseExact(o.Id, "N")), o.Label));
        if (gameOptions >= available.Count)
        {
            Assert.Equal(available.Order(), ids.Order());
        }
    }

    [Fact]
    public void A_poll_over_a_pool_of_600_games_offers_a_recorded_sample_of_game_options()
    {
        static Scenario Pool(int seed)
        {
            var s = Scenario.New(Rules(gameOptions: 5), seed).WithCategory("Horror");
            for (var i = 1; i <= 600; i++)
            {
                s.WithGame($"Игра {i}", 6, "Horror");
            }

            return s.WithPlayers("Вася", "Петя").WithPoints("Петя", 10);
        }

        var s = Pool(7);
        var poll = s.Start(EffectPoll(s_coinsToLeader, ChoiceSource.Games));

        Assert.Equal(5, poll.Options.Count);
        Assert.Equal(5, poll.Options.Select(o => o.Id).Distinct().Count());

        // The sample is in the log: a replay keeps it, the same seed and commands draw the same one
        Assert.Equal(poll.Options, SeasonEngine.Replay(s.StoredLog).Polls[poll.PollId].Options);
        Assert.Equal(poll.Options, Pool(7).Start(EffectPoll(s_coinsToLeader, ChoiceSource.Games)).Options);
    }

    [Fact]
    public void The_voted_game_is_the_leaders_next_roll()
    {
        var s = Season();
        var poll = s.Start(EffectPoll(s_gameToLeader, ChoiceSource.Games));
        var persona = N(s.GameId("Persona"));
        Assert.Contains(persona, poll.Options.Select(o => o.Id));
        s.Voted(poll.PollId, "Вася", persona);

        s.CloseByTimer(poll.PollId);

        Assert.Equal(persona, Assert.Single(s.LastEvents<PollClosed>()).Result);
        s.Roll("Петя");
        Assert.Equal(s.GameId("Persona"), s.Player("Петя").Offer!.GameId);
    }

    // ---- S5.8-14 (EV21): players — the active ones, without the first finisher ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_poll_over_players_offers_the_active_players_but_the_first_finisher(bool approved)
    {
        var s = Season(mapLength: 4);
        var run = s.Finish("Маша");
        if (approved)
        {
            s.Approve(run);
        }

        s.Inactive("Коля");

        var poll = s.Start(ListPoll() with { Options = new ChoiceOptionsSpec { From = ChoiceSource.Players } });

        Assert.Equal(new[] { "Вася", "Петя" }.Select(p => N(s.PlayerId(p))).Order(), poll.Options.Select(o => o.Id).Order());
        Assert.All(poll.Options, o => Assert.Equal(s.PlayerName(Guid.ParseExact(o.Id, "N")), o.Label));
    }

    [Fact]
    public void A_poll_over_categories_skips_a_category_whose_games_are_gone()
    {
        var s = Season();
        s.DeleteGame("Need for Speed");

        var poll = s.Start(ContentPoll());

        Assert.Equal(["Horror", "RPG"], poll.Options.Select(o => o.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_admin_poll_with_fewer_than_two_options_is_refused()
    {
        var s = Season();
        s.DeleteGame("Need for Speed").DeleteGame("Skyrim").DeleteGame("Persona");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ContentPoll())), RejectionCodes.PollNoOptions);

        // Players: only Вася is active
        s.Inactive("Петя").Inactive("Маша").Inactive("Коля");
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new StartPoll(ListPoll() with { Options = new ChoiceOptionsSpec { From = ChoiceSource.Players } })), RejectionCodes.PollNoOptions);
    }

    // ---- S5.8-19 (EV21): anonymity — the voter is stored, the flags travel with the vote ----

    [Fact]
    public void An_anonymous_vote_keeps_the_voter_in_the_event_and_hides_the_live_counts()
    {
        var s = Season(Rules(showLiveCountsDefault: true));
        var poll = s.Start(ListPoll(anonymous: true));
        Assert.Equal((true, false), (poll.Anonymous, poll.ShowLiveCounts));

        s.Voted(poll.PollId, "Вася", "Финиш");
        Assert.Equal(new VoteCast(poll.PollId, s.Player("Вася").UserId, "Финиш", true, false), Assert.Single(s.Last.Events));

        s.Voted(poll.PollId, "Вася", "Дроп");
        Assert.Equal(new VoteChanged(poll.PollId, s.Player("Вася").UserId, "Дроп", true, false), Assert.Single(s.Last.Events));
        Assert.Equal([new Vote(s.Player("Вася").UserId, "Дроп")], s.State.Polls[poll.PollId].Votes);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void Live_counts_of_a_named_poll_follow_the_definition_then_the_ruleset(bool? showLiveCounts, bool byDefault, bool expected)
    {
        var s = Season(Rules(showLiveCountsDefault: byDefault));
        var poll = s.Start(ListPoll(showLiveCounts: showLiveCounts));
        Assert.Equal((false, expected), (poll.Anonymous, poll.ShowLiveCounts));

        s.Voted(poll.PollId, "Маша", "Реролл");

        Assert.Equal(new VoteCast(poll.PollId, s.Player("Маша").UserId, "Реролл", false, expected), Assert.Single(s.Last.Events));
    }

    [Fact]
    public void An_anonymous_poll_with_live_counts_is_refused()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ListPoll(anonymous: true, showLiveCounts: true))), RejectionCodes.ContentInvalid);
    }

    // ---- The result's targets (D-512, D-537): leader, a random active player, everyone ----

    [Fact]
    public void The_result_on_everyone_is_queued_in_the_closing_command_and_settled_for_all()
    {
        var s = Season();
        var effect = new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.All },
            Actions = [Coins(2), new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$result")]) }],
        };
        var poll = s.Start(EffectPoll(effect));
        s.Voted(poll.PollId, "Вася", "RPG");
        var coins = Names.ToDictionary(p => p, p => s.Player(p).Coins);

        s.CloseByTimer(poll.PollId);

        // The closing command only queues the result (D-537): nobody got anything yet
        ScenarioAssert.Accepted(s);
        Assert.Equal("RPG", Assert.Single(s.LastEvents<PollClosed>()).Result);
        var queued = Assert.Single(s.LastEvents<ForAllQueued>());
        Assert.Equal((ForAllSource.PollResult, poll.PollId, "poll-result", effect), (queued.Source, queued.Reference, queued.ObjectId, queued.Effect));
        Assert.Equal("RPG", queued.Parameters!["result"]);
        Assert.Equal(Names.Select(s.PlayerId).Order(), queued.Players);
        Assert.Empty(s.LastEvents<ObjectGiven>());

        s.SettleAll();

        Assert.All(Names, p => Assert.Equal((p, coins[p] + 2, 1), (p, s.Player(p).Coins, s.Genres(p).Count(g => g == "RPG"))));
    }

    [Fact]
    public void The_result_on_a_random_active_player_goes_to_one_active_player()
    {
        var s = Season();
        s.Inactive("Коля");
        var poll = s.Start(ListEffectPoll(new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.RandomActive }, Actions = [Coins(3)] }));
        s.Voted(poll.PollId, "Вася", "да");
        var coins = Names.ToDictionary(p => p, p => s.Player(p).Coins);

        s.CloseByTimer(poll.PollId);

        ScenarioAssert.Accepted(s);
        var gained = Names.Where(p => s.Player(p).Coins != coins[p]).ToList();
        var winner = Assert.Single(gained);
        Assert.NotEqual("Коля", winner);
        Assert.Equal(coins[winner] + 3, s.Player(winner).Coins);
    }

    // ---- What a result effect may be (D-512, D-519, D-703 (3)) ----

    [Theory]
    [InlineData("move")]
    [InlineData("teleport")]
    [InlineData("caseMove")]
    public void A_result_on_everyone_may_not_move_or_teleport(string what)
    {
        // S5.8-21, D-519: 16 pushes with their cells do not fit the chain
        var all = new TargetSpec { Selector = TargetSelector.All };
        var effect = what switch
        {
            "move" => new EffectSpec { Target = all, Actions = [new MoveAction { Steps = ContentValue.Of(1) }] },
            "teleport" => new EffectSpec { Target = all, Actions = [new TeleportAction { Cell = "start" }] },
            _ => new EffectSpec
            {
                Target = all,
                Outcomes = new OutcomesSpec
                {
                    Roll = ContentValue.TryParse("1d6"),
                    Cases = [new OutcomeCase { From = 1, To = 6, Actions = [new MoveAction { Steps = ContentValue.Of(1) }] }],
                },
            },
        };
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ListEffectPoll(effect))), RejectionCodes.ContentInvalid);
    }

    [Fact]
    public void A_result_on_the_leader_may_move_and_on_everyone_may_give()
    {
        var s = Season();

        s.Act(new StartPoll(ListEffectPoll(new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.Leader },
            Actions = [new MoveAction { Steps = ContentValue.Of(1) }],
        })));
        ScenarioAssert.Accepted(s);

        s.Act(new StartPoll(ListEffectPoll(new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.All }, Actions = [Coins(1)] })));
        ScenarioAssert.Accepted(s);
    }

    public static TheoryData<string> RefusedResults => new() { "no target", "self", "chosen", "higherPoints", "asks", "outcome asks" };

    [Theory]
    [MemberData(nameof(RefusedResults))]
    public void A_result_effect_without_a_system_target_or_that_asks_is_refused(string what)
    {
        // A result has nobody who used it: only leader, randomActive and all; and nobody to ask (D-703 (3))
        static EffectSpec On(TargetSelector? selector, params ActionSpec[] actions) =>
            new() { Target = selector is { } x ? new TargetSpec { Selector = x } : null, Actions = [.. actions] };
        var effect = what switch
        {
            "no target" => On(null, Coins(1)),
            "self" => On(TargetSelector.Self, Coins(1)),
            "chosen" => On(TargetSelector.Chosen, Coins(1)),
            "higherPoints" => On(TargetSelector.HigherPoints, Coins(1)),
            "asks" => On(TargetSelector.Leader, new RequestChoiceAction { Prompt = "Какой?", Options = new ChoiceOptionsSpec { List = ["а", "б"] } }),
            _ => new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.All },
                Outcomes = new OutcomesSpec
                {
                    Choice = new OutcomeChoice { Prompt = "Что?", Options = new ChoiceOptionsSpec { List = ["а", "б"] } },
                    Cases = [new OutcomeCase { Option = "а", Actions = [Coins(1)] }, new OutcomeCase { Option = "б" }],
                },
            },
        };
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ListEffectPoll(effect))), RejectionCodes.ContentInvalid);
    }

    // ---- S5.8-22 (EV35): the flag; D-544: an effect needs items ----

    [Fact]
    public void Polls_off_refuse_starting_voting_and_closing()
    {
        var s = Season(TestRuleset.Create().WithEconomy());

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ContentPoll())), RejectionCodes.FeatureDisabled);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ListPoll())), RejectionCodes.FeatureDisabled);
        var some = Guid.Parse("5f000000-0000-0000-0000-000000000001");
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(some, "Вася", "Horror"), RejectionCodes.FeatureDisabled);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ClosePoll(some)), RejectionCodes.FeatureDisabled);
        Assert.Empty(s.State.Polls);
    }

    [Fact]
    public void A_poll_whose_result_acts_needs_items_and_a_nomination_does_not()
    {
        var rules = Rules() with { Features = Rules().Features with { Items = false } };
        var s = Scenario.New(rules).WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Skyrim", 6, "RPG").WithPlayers("Вася", "Петя");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new StartPoll(ListEffectPoll(new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Leader }, Actions = [Coins(1)] }))),
            RejectionCodes.FeatureDisabled);

        s.Start(ListPoll());
    }

    [Fact]
    public void A_poll_is_not_started_in_a_draft()
    {
        var s = Scenario.New(Rules()).AsDraft().WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Skyrim", 6, "RPG").WithPlayers("Вася", "Петя");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartPoll(ListPoll())), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void An_event_with_a_vote_is_published_only_with_polls_on_and_a_vote_belongs_to_events_only()
    {
        // Н-15: an event with a vote is not turned into a plain question while polls are off; it is not published
        var pack = new ContentPack { Objects = [EventPollTests.LeaderPick] };

        var off = ContentPublishing.Check(pack, events: true, polls: false);
        Assert.Contains(off, e => e.Path.StartsWith("$.objects[0]", StringComparison.Ordinal) && e.Path.EndsWith(".vote", StringComparison.Ordinal));
        Assert.DoesNotContain(ContentPublishing.Check(pack, events: true, polls: true), e => e.Path.EndsWith(".vote", StringComparison.Ordinal));

        // The same question with a vote in an item: refused even with polls on — an item asks its user before the use
        var item = EconomyScenario.Examples()["orange"] with { Id = "vote-item", Effect = EventPollTests.LeaderPick.Effect };
        Assert.Contains(
            ContentPublishing.Check(new ContentPack { Objects = [item] }, events: true, polls: true),
            e => e.Path.EndsWith(".vote", StringComparison.Ordinal));

        // And the engine refuses the publication, as the check says
        var s = Scenario.New(TestRuleset.Create().WithEvents()).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new PublishContent(pack, "Ивент с голосованием")), RejectionCodes.ContentInvalid);
    }

    // ---- S5.8-23 (EV38): the undo of a vote and of the close ----

    [Fact]
    public void A_vote_is_undone_only_after_the_close_that_counted_it()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");
        var vote = s.LastCommandId;
        s.Voted(poll.PollId, "Маша", "RPG");
        s.Voted(poll.PollId, "Коля", "Horror");
        s.CloseByTimer(poll.PollId);
        var close = s.LastCommandId;
        Assert.Equal(["Horror"], s.Genres("Петя"));

        // The close depends on the vote it counted
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(vote, "Ошибка админа")), RejectionCodes.UndoDependents);
        Assert.Contains(close, s.Last.Rejection!.Related);

        // The close is undone: the poll is open again, the result's effect is taken back
        s.Act(new UndoCommand(close, "Ошибка админа"));
        ScenarioAssert.Accepted(s);
        var reopened = s.State.Polls[poll.PollId];
        Assert.Equal((PollStatus.Open, (string?)null), (reopened.Status, reopened.Result));
        Assert.Empty(s.Genres("Петя"));

        // Then the vote: Вася has none
        s.Act(new UndoCommand(vote, "Ошибка админа"));
        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(s.State.Polls[poll.PollId].Votes, v => v.UserId == s.Player("Вася").UserId);
        Assert.Equal(2, s.State.Polls[poll.PollId].Votes.Count);
    }

    [Fact]
    public void Votes_of_different_users_do_not_depend_on_each_other()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");
        var first = s.LastCommandId;
        s.Voted(poll.PollId, "Маша", "Horror");

        s.Act(new UndoCommand(first, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Vote(s.Player("Маша").UserId, "Horror")], s.State.Polls[poll.PollId].Votes);

        // Вася votes again after the undo
        s.Voted(poll.PollId, "Вася", "RPG");
        Assert.IsType<VoteCast>(Assert.Single(s.Last.Events));
    }

    [Fact]
    public void The_undo_of_a_start_takes_the_poll_away_and_waits_for_its_votes()
    {
        var s = Season();
        var poll = s.Start(ContentPoll());
        var start = s.LastCommandId;
        s.Voted(poll.PollId, "Вася", "Horror");
        var vote = s.LastCommandId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(start, "Ошибка админа")), RejectionCodes.UndoDependents);
        Assert.Contains(vote, s.Last.Rejection!.Related);

        s.Act(new UndoCommand(vote, "Ошибка админа"));
        s.Act(new UndoCommand(start, "Ошибка админа"));
        ScenarioAssert.Accepted(s);
        Assert.False(s.State.Polls.TryGetValue(poll.PollId, out var left) && left.Status == PollStatus.Open);
    }
}

/// <summary>Positions of events in the last command, for order checks.</summary>
internal static class PollEventOrder
{
    public static int IndexOf<T>(this Scenario s) where T : IGameEvent
    {
        for (var i = 0; i < s.Last.Events.Count; i++)
        {
            if (s.Last.Events[i] is T)
            {
                return i;
            }
        }

        return -1;
    }
}
