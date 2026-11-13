using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Polls.PollSetup;

namespace GameEvent.Engine.Tests.Polls;

/// <summary>
/// A poll from an event — «выберите игру лидеру» (task 5.8, SPEC «Голосования»: «Используются … для ивентов вида
/// „выберите игру лидеру“», EV23; D-512). The event's <c>requestChoice</c> with <c>vote</c> asks the voters, not the drawer:
/// its effect is suspended with the poll's id (<see cref="EffectSuspended.PollId"/>), the drawer has no pending choice and
/// plays on (Н-14), and <see cref="PollStarted"/> of source <see cref="PollSource.Event"/> offers a sample of the games
/// available to the event's target. The close with a result resumes the effect with it as <c>$choice</c>: the leader's next
/// roll is the voted game (<c>modifyNextRoll.filter.games</c>); a game gone by the roll drops the filter with a record and
/// the roll goes the usual way. Written by <c>test-author</c> from the SPEC, before the implementation.
/// </summary>
public class EventPollTests
{
    private const string Prompt = "Какую игру выберем лидеру?";

    /// <summary>The event «Выберите игру лидеру»: the target is the leader at the draw; the voters pick one of his games.</summary>
    internal static ObjectDefinition LeaderPick { get; } = new()
    {
        Id = "leader-pick",
        Kind = ObjectKind.Event,
        Name = "Выберите игру лидеру",
        Description = "Игроки голосуют, какую игру лидер возьмёт следующей.",
        Rarity = Rarity.Common,
        Effect = new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.Leader },
            Actions =
            [
                new RequestChoiceAction
                {
                    Prompt = Prompt,
                    Options = new ChoiceOptionsSpec { From = ChoiceSource.Games },
                    Vote = new PollVoteSpec { Voters = PollVoters.Players, Anonymous = false, ClosesInHours = 24 },
                },
                new ModifyNextRollAction { Filter = new GameFilterSpec { Games = ["$choice"] } },
            ],
        },
    };

    private static readonly string[] s_cells = ["start", "a", "e", "b", .. Enumerable.Range(1, 20).Select(i => $"c{i}"), "finish"];

    /// <summary>
    /// <c>start → a → e → b → c1 … c20 → finish</c>, <c>e</c> an event cell of the deck <c>ask</c> (a good deck of
    /// <paramref name="asking"/>). Horror: Silent Hill, Persona; RPG: Disco Elysium. Петя leads with 50 points; Вася on
    /// <c>a</c> with an orange (one step).
    /// </summary>
    internal static Scenario Season(ObjectDefinition? asking = null, Func<Ruleset, Ruleset>? rules = null)
    {
        var ruleset = TestRuleset.Create().WithEvents().WithPolls();
        var s = Scenario.New(rules is null ? ruleset : rules(ruleset))
            .WithMap(MapBuilder.New().EventCell("e", "ask").Path(s_cells).Build())
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Persona", 9, "Horror").WithGame("Disco Elysium", 30, "RPG")
            .WithPlayers(Names);
        var pick = asking ?? LeaderPick;
        ObjectDefinition[] objects = [pick, EconomyScenario.Examples()["orange"], EconomyScenario.Examples()["forced-genre"]];
        s.WithContent(new ContentPack
        {
            Objects = [.. objects.OrderBy(o => o.Id, StringComparer.Ordinal)],
            Wheels = [EventScenario.Deck(EventKind.Good, "ask", pick.Id)],
        });
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Позиция для теста", CellId: "a"));
        s.Give("Вася", "orange");
        return s.WithPoints("Петя", 50);
    }

    /// <summary>Вася steps onto the event cell (ticket 0 of the deck); returns the poll the event started.</summary>
    internal static PollStarted Draw(Scenario s)
    {
        s.NextRandom(0).Used("Вася", "orange");
        Assert.Equal("e", s.Player("Вася").CellId);
        return Assert.Single(s.LastEvents<PollStarted>());
    }

    private static Scenario At(Scenario s, string player, string cell)
    {
        s.Act(new AdjustPlayer(s.PlayerId(player), "Позиция для теста", CellId: cell));
        ScenarioAssert.Accepted(s);
        return s;
    }

    [Fact]
    public void A_leader_who_finished_first_while_the_poll_was_open_is_not_reached_by_its_result()
    {
        // SPEC «Цели», D-723: the target picked at the draw is checked again at the close (review of 5.8)
        var s = Season();
        var poll = Draw(s);
        var persona = N(s.GameId("Persona"));
        s.Voted(poll.PollId, "Маша", persona);
        At(s, "Петя", "c20").Finish("Петя");
        Assert.True(s.Player("Петя").Finish is not null);

        s.CloseByTimer(poll.PollId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(persona, Assert.Single(s.LastEvents<PollClosed>()).Result);
        Assert.Single(s.LastEvents<EffectResumed>());
        Assert.Empty(s.LastEvents<NextRollModified>());
        Assert.Empty(s.Player("Вася").Suspended);
    }

    [Fact]
    public void Undoing_the_close_of_an_events_poll_takes_the_result_back_and_the_poll_waits_again()
    {
        // D-722: the close and the continuation are one command
        var s = Season();
        var poll = Draw(s);
        var persona = N(s.GameId("Persona"));
        s.Voted(poll.PollId, "Маша", persona);
        s.CloseByTimer(poll.PollId);
        var close = s.LastCommandId;
        Assert.Single(s.LastEvents<NextRollModified>());

        s.Act(new Engine.Undo.UndoCommand(close, "Закрыли по ошибке"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(PollStatus.Open, s.State.Polls[poll.PollId].Status);
        Assert.Contains(s.Player("Вася").Suspended, x => x.PollId == poll.PollId);
        Assert.Empty(s.Player("Петя").Wallet.NextRoll);
        Assert.Null(s.Player("Вася").Choice);
    }

    [Fact]
    public void With_events_switched_off_an_events_poll_closes_without_playing_on()
    {
        // D-564, D-723: the answer of an event waits while events are off, so the result ends the effect without its actions
        var s = Season();
        var poll = Draw(s);
        s.Voted(poll.PollId, "Маша", N(s.GameId("Persona")));
        s.Act(new Engine.Rulesets.ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { Events = false } }));
        ScenarioAssert.Accepted(s);

        s.CloseByTimer(poll.PollId);

        ScenarioAssert.Accepted(s);
        Assert.NotNull(Assert.Single(s.LastEvents<PollClosed>()).Result);
        Assert.Empty(s.LastEvents<EffectResumed>());
        Assert.Empty(s.Player("Вася").Suspended);
    }

    // ---- S5.8-15 (EV23): the event asks the voters and the leader's next roll is the voted game ----

    [Fact]
    public void An_events_vote_suspends_its_effect_and_the_result_is_the_leaders_next_game()
    {
        var s = Season();
        var drawnAt = s.Clock.UtcNow;

        var poll = Draw(s);

        ScenarioAssert.Accepted(s);
        Assert.Equal("leader-pick", Assert.Single(s.LastEvents<EventDrawn>()).ObjectId);
        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal((s.PlayerId("Вася"), "leader-pick", poll.PollId), (suspended.PlayerId, suspended.ObjectId, suspended.PollId));

        // The poll: of the event, naming the drawer, the suspension, the event and the leader whose games are offered
        Assert.Equal(PollSource.Event, poll.Source);
        Assert.Equal(Prompt, poll.Question);
        Assert.Equal((s.PlayerId("Вася"), suspended.ChoiceId, "leader-pick", EventKind.Good, s.PlayerId("Петя")),
            (poll.PlayerId, poll.ChoiceId, poll.ObjectId, poll.Kind, poll.TargetId));
        Assert.Equal((PollVoters.Players, false), (poll.Voters, poll.Anonymous));
        Assert.Equal(drawnAt.AddHours(24), poll.ClosesAt);
        Assert.Null(poll.OnResult);
        Assert.Equal(new[] { "Silent Hill", "Persona", "Disco Elysium" }.Select(t => N(s.GameId(t))).Order(), poll.Options.Select(o => o.Id).Order());
        Assert.True(s.State.Polls[poll.PollId].HasEffect);

        // The drawer is not asked: the question is the voters'
        Assert.Null(s.Player("Вася").Choice);
        Assert.Contains(s.Player("Вася").Suspended, x => x.PollId == poll.PollId);

        var persona = N(s.GameId("Persona"));
        s.Voted(poll.PollId, "Петя", persona).Voted(poll.PollId, "Маша", persona).Voted(poll.PollId, "Коля", N(s.GameId("Silent Hill")));

        s.CloseByTimer(poll.PollId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(persona, Assert.Single(s.LastEvents<PollClosed>()).Result);
        Assert.Equal(new EffectResumed(s.PlayerId("Вася"), suspended.ChoiceId, persona), Assert.Single(s.LastEvents<EffectResumed>()));
        Assert.True(s.IndexOf<PollClosed>() < s.IndexOf<EffectResumed>());
        var modified = Assert.Single(s.LastEvents<NextRollModified>());
        Assert.Equal(s.PlayerId("Петя"), modified.PlayerId);
        Assert.Equal([persona], modified.Modifier.Filter!.Games!.Value);
        Assert.Empty(s.Player("Вася").Suspended);

        s.Roll("Петя");

        Assert.Equal(s.GameId("Persona"), s.Player("Петя").Offer!.GameId);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void The_event_as_content_json_reads_as_the_same_object()
    {
        var json = """
            {
              "id": "leader-pick", "kind": "event", "name": "Выберите игру лидеру",
              "description": "Игроки голосуют, какую игру лидер возьмёт следующей.",
              "rarity": "common", "price": null, "hostile": false,
              "effect": {
                "target": { "selector": "leader" },
                "actions": [
                  { "type": "requestChoice", "prompt": "Какую игру выберем лидеру?", "options": { "from": "games" },
                    "vote": { "voters": "players", "anonymous": false, "closesInHours": 24 } },
                  { "type": "modifyNextRoll", "filter": { "games": ["$choice"] } }
                ]
              }
            }
            """;

        var parsed = ContentJson.Parse<ObjectDefinition>(json);

        Assert.Equal(LeaderPick.Effect, parsed.Effect);
    }

    // ---- S5.8-16 (EV23): the voted game is gone by the roll ----

    [Fact]
    public void A_voted_game_gone_by_the_roll_drops_the_filter_with_a_record_and_the_roll_goes_the_usual_way()
    {
        var s = Season();
        var poll = Draw(s);
        var persona = N(s.GameId("Persona"));
        s.Voted(poll.PollId, "Маша", persona);
        s.CloseByTimer(poll.PollId);
        ScenarioAssert.Accepted(s);

        // Коля completes Persona before Петя rolls: it is passed in the season, unavailable to all
        At(s, "Коля", "b");
        s.RollTitle("Коля", "Persona").Start("Коля").Complete("Коля");
        ScenarioAssert.Accepted(s);

        s.Roll("Петя");

        var applied = Assert.Single(s.LastEvents<RollModifiersApplied>());
        var dropped = Assert.Single(applied.Dropped);
        Assert.Equal([persona], dropped.Filter!.Games!.Value);
        Assert.NotEqual(s.GameId("Persona"), s.Player("Петя").Offer!.GameId);

        // Dropped for good: not waiting for the next roll either
        Assert.DoesNotContain(s.Player("Петя").Wallet.NextRoll, m => m.Filter?.Games is not null);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void A_voted_game_still_available_is_no_miss()
    {
        var s = Season();
        var poll = Draw(s);
        var silentHill = N(s.GameId("Silent Hill"));
        s.Voted(poll.PollId, "Маша", silentHill);
        s.CloseByTimer(poll.PollId);

        s.Roll("Петя");

        Assert.All(s.LastEvents<RollModifiersApplied>(), a => Assert.Empty(a.Dropped));
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Петя").Offer!.GameId);
    }

    [Fact]
    public void The_games_of_an_events_poll_are_the_targets_available_ones()
    {
        // Петя holds Silent Hill in play: occupied, so not offered; the rest are
        var s = Season();
        At(s, "Петя", "b");
        s.RollTitle("Петя", "Silent Hill").Start("Петя");

        var poll = Draw(s);

        Assert.Equal(new[] { "Persona", "Disco Elysium" }.Select(t => N(s.GameId(t))).Order(), poll.Options.Select(o => o.Id).Order());
    }

    // ---- The rest of the mechanism, on an event that asks the voters for a genre (no filter.games needed) ----

    /// <summary>
    /// «Жанр голосованием»: the drawer gets the genre the voters pick (target self, options from the categories) — the same
    /// suspension by the poll as <see cref="LeaderPick"/>, checked apart from <c>modifyNextRoll.filter.games</c>.
    /// </summary>
    internal static ObjectDefinition GenreVote(PollVoteSpec? vote = null) => new()
    {
        Id = "genre-vote",
        Kind = ObjectKind.Event,
        Name = "Жанр голосованием",
        Description = "Игроки голосуют, какой жанр достанется вытянувшему.",
        Rarity = Rarity.Common,
        Effect = new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.Self },
            Actions =
            [
                new RequestChoiceAction
                {
                    Prompt = "Какой жанр ему подкинем?",
                    Options = new ChoiceOptionsSpec { From = ChoiceSource.Categories },
                    Vote = vote ?? new PollVoteSpec { Voters = PollVoters.Players, Anonymous = false, ClosesInHours = 24 },
                },
                new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$choice")]) },
            ],
        },
    };

    [Fact]
    public void An_events_vote_on_a_genre_suspends_the_effect_and_resumes_it_with_the_result()
    {
        var s = Season(GenreVote());

        var poll = Draw(s);

        var suspended = Assert.Single(s.LastEvents<EffectSuspended>());
        Assert.Equal(poll.PollId, suspended.PollId);
        Assert.Equal(s.PlayerId("Вася"), poll.PlayerId);
        Assert.Equal(suspended.ChoiceId, poll.ChoiceId);
        Assert.Equal(("genre-vote", PollSource.Event, EventKind.Good), (poll.ObjectId, poll.Source, poll.Kind));

        // No games offered: the target of the offered games is not written (or is the drawer, the effect's target)
        Assert.True(poll.TargetId is null || poll.TargetId == s.PlayerId("Вася"), $"TargetId {poll.TargetId}");
        Assert.Equal(["Horror", "RPG"], poll.Options.Select(o => o.Id).Order(StringComparer.Ordinal));
        Assert.Null(s.Player("Вася").Choice);
        Assert.Empty(s.LastEvents<ObjectGiven>());
        s.Voted(poll.PollId, "Петя", "RPG").Voted(poll.PollId, "Маша", "RPG");

        s.CloseByTimer(poll.PollId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new EffectResumed(s.PlayerId("Вася"), suspended.ChoiceId, "RPG"), Assert.Single(s.LastEvents<EffectResumed>()));
        Assert.Equal(["RPG"], s.Genres("Вася"));
        Assert.Empty(s.Player("Вася").Suspended);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    // ---- S5.8-17 (EV23): the event's poll does not hold the drawer's turn (Н-14) ----

    [Fact]
    public void The_drawer_plays_on_while_the_events_poll_is_open()
    {
        var s = Season(GenreVote());
        var poll = Draw(s);

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.NotNull(s.Player("Вася").Offer);
        Assert.Equal(PollStatus.Open, s.State.Polls[poll.PollId].Status);
        s.Roll("Петя");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void An_events_poll_without_votes_ends_the_effect_without_its_actions()
    {
        var s = Season(GenreVote());
        var poll = Draw(s);

        s.CloseByTimer(poll.PollId);

        ScenarioAssert.Accepted(s);
        Assert.Null(Assert.Single(s.LastEvents<PollClosed>()).Result);
        Assert.Empty(s.LastEvents<EffectResumed>());
        Assert.Empty(s.LastEvents<ObjectGiven>());
        Assert.Empty(s.Player("Вася").Suspended);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Empty(s.Genres("Вася"));
    }

    [Fact]
    public void An_events_poll_closed_by_the_admin_resumes_the_effect_too()
    {
        var s = Season(GenreVote());
        var poll = Draw(s);
        s.Voted(poll.PollId, "Маша", "Horror");

        s.Act(new ClosePoll(poll.PollId));

        ScenarioAssert.Accepted(s);
        Assert.Equal(PollCloseReason.Admin, Assert.Single(s.LastEvents<PollClosed>()).Reason);
        Assert.Equal("Horror", Assert.Single(s.LastEvents<EffectResumed>()).OptionId);
        Assert.Equal(["Horror"], s.Genres("Вася"));
    }

    [Fact]
    public void An_events_poll_with_no_option_asks_nobody()
    {
        // D-512: an event's poll with no options just skips the question (an admin poll is refused instead)
        var s = Season(GenreVote());
        s.DeleteGame("Silent Hill").DeleteGame("Persona").DeleteGame("Disco Elysium");

        s.NextRandom(0).Used("Вася", "orange");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PollStarted>());
        Assert.Empty(s.State.Polls);
        Assert.Null(s.Player("Вася").Choice);
        Assert.DoesNotContain(s.Player("Вася").Suspended, x => x.PollId is not null);
    }

    [Fact]
    public void An_events_poll_with_no_game_to_offer_asks_nobody()
    {
        // The same for «выберите игру лидеру» when no game is left
        var s = Season();
        s.DeleteGame("Silent Hill").DeleteGame("Persona").DeleteGame("Disco Elysium");

        s.NextRandom(0).Used("Вася", "orange");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PollStarted>());
        Assert.Null(s.Player("Вася").Choice);
        Assert.DoesNotContain(s.Player("Вася").Suspended, x => x.PollId is not null);
    }

    [Fact]
    public void An_events_poll_for_everyone_takes_its_flags_from_the_vote()
    {
        var everyone = GenreVote(new PollVoteSpec { Voters = PollVoters.Everyone, Anonymous = true, ClosesInHours = 6 });
        var s = Season(everyone, r => r with { Social = r.Social with { SpectatorsCanVote = true } });
        var drawnAt = s.Clock.UtcNow;

        var poll = Draw(s);

        Assert.Equal((PollVoters.Everyone, true, false, drawnAt.AddHours(6)), (poll.Voters, poll.Anonymous, poll.ShowLiveCounts, poll.ClosesAt));
        s.Act(new CastVote(poll.PollId, Spectator, "Horror"));
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void An_events_anonymous_vote_with_live_counts_is_not_published()
    {
        var pack = new ContentPack
        {
            Objects = [GenreVote(new PollVoteSpec { Voters = PollVoters.Players, Anonymous = true, ClosesInHours = 6, ShowLiveCounts = true })],
        };

        Assert.Contains(ContentPublishing.Check(pack, events: true, polls: true), e => e.Path.Contains(".vote", StringComparison.Ordinal));
    }

    // ---- EV42: the first finisher does not vote in an event's poll — its result acts on players ----

    [Fact]
    public void The_first_finisher_does_not_vote_in_an_events_poll()
    {
        var s = Season(GenreVote());
        At(s, "Маша", "c20");
        s.RollTitle("Маша", "Silent Hill").Start("Маша").Complete("Маша");
        Assert.NotNull(s.Player("Маша").Finish);
        var poll = Draw(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Маша", "Horror"), RejectionCodes.PollFrozenVoter);
        s.Voted(poll.PollId, "Коля", "Horror");
    }
}
