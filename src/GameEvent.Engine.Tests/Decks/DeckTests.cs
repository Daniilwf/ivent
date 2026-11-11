using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Decks;

/// <summary>
/// Decks (SPEC «Колёса и колоды — одна сущность», D-501): a deck is a wheel with a kind of events; a draw picks by weight
/// among the live entries, the drawn event has the kind of its deck, the result is in the log (EV2, EV3). What a pack may hold
/// with events on and off (D-532), and <c>drawEvent</c> of an item (D-504).
/// </summary>
public class DeckTests
{
    private static readonly ObjectDefinition s_drawGood = EconomyScenario.Examples()["orange"] with
    {
        Id = "fortune",
        Name = "Печенье с предсказанием",
        Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new DrawEventAction { Deck = "good" }] },
    };

    private static Scenario Season(ContentPack pack, MapGraph? map = null)
    {
        var s = EventScenario.New()
            .WithMap(map ?? MapBuilder.New().EventCell("e", "lucky").Path("start", "a", "e", "b", "c", "d", "f", "finish").Build())
            .WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася", "Петя");
        return s.WithContent(pack);
    }

    private static Scenario To(Scenario s, string cell)
    {
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Позиция для теста", CellId: cell));
        return s;
    }

    private static ContentPack Pack(params WheelDefinition[] decks) =>
        EventScenario.Pack(["tailwind", "horror-week", "gamble", "forced-genre", "orange"], decks);

    [Fact]
    public void The_kind_of_an_event_is_the_kind_of_its_deck()
    {
        // The same event in a bad deck of its own: drawn from there it is a bad event
        var s = To(Season(Pack(EventScenario.Deck(EventKind.Bad, "lucky", "tailwind"), EventScenario.Deck(EventKind.Good, null, "tailwind"))), "a");
        s.Give("Вася", "orange");

        s.NextRandom(0).Used("Вася", "orange");

        Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "lucky", "tailwind", EventKind.Bad, EventSource.Cell), Assert.Single(s.LastEvents<EventDrawn>()));
    }

    [Theory]
    [InlineData(0, "tailwind")]
    [InlineData(1, "horror-week")]
    [InlineData(3, "horror-week")]
    public void A_draw_picks_by_weight_in_the_order_of_the_deck(int ticket, string drawn)
    {
        var s = To(Season(Pack(EventScenario.Deck(EventKind.Good, "lucky", ("tailwind", 1), ("horror-week", 3)))), "a");
        s.Give("Вася", "orange");

        s.NextRandom(ticket).Used("Вася", "orange");

        Assert.Equal(drawn, Assert.Single(s.LastEvents<EventDrawn>()).ObjectId);
    }

    [Fact]
    public void An_entry_of_weight_zero_is_never_drawn()
    {
        var s = To(Season(Pack(EventScenario.Deck(EventKind.Good, "lucky", ("horror-week", 0), ("tailwind", 1)))), "a");
        s.Give("Вася", "orange");

        // One ticket over the live weight 1: always the tailwind
        s.NextRandom(0).Used("Вася", "orange");

        Assert.Equal("tailwind", Assert.Single(s.LastEvents<EventDrawn>()).ObjectId);
        Assert.Equal(0, s.Random.ScriptedLeft);
    }

    [Fact]
    public void A_draw_is_written_as_its_result_and_a_replay_of_the_log_gives_the_same_state()
    {
        var s = To(Season(Pack(EventScenario.Deck(EventKind.Good, "lucky", ("tailwind", 1), ("horror-week", 1)))), "a");
        s.Give("Вася", "orange");

        s.NextRandom(1).Used("Вася", "orange");

        Assert.Equal("horror-week", Assert.Single(s.LastEvents<EventDrawn>()).ObjectId);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void The_draw_event_of_an_item_draws_from_the_deck_and_plays_the_event_on_its_user()
    {
        var pack = Pack(EventScenario.Deck(EventKind.Good, null, "tailwind"));
        var s = Season(pack with { Objects = [.. pack.Objects, s_drawGood] });
        s.Give("Вася", "fortune");

        s.NextRandom(0).Used("Вася", "fortune");

        Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "good", "tailwind", EventKind.Good, EventSource.Item), Assert.Single(s.LastEvents<EventDrawn>()));
        Assert.Single(s.Player("Вася").Wallet.NextDice);
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void With_events_off_the_draw_event_of_an_item_is_a_manual_effect_as_before()
    {
        var s = Scenario.New(TestRuleset.Create().WithEconomy()).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");
        s.WithContent(new ContentPack { Objects = [s_drawGood] });
        s.Give("Вася", "fortune");

        s.Used("Вася", "fortune");

        Assert.Empty(s.LastEvents<EventDrawn>());
        var manual = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal(((EventKind?)EventKind.Good, ManualEffectSource.Item), (manual.DrawEvent, manual.Source));
    }

    [Fact]
    public void With_events_off_a_pack_with_events_or_decks_is_refused()
    {
        var s = Scenario.New(TestRuleset.Create().WithEconomy()).WithPlayers("Вася");

        s.Act(new PublishContent(EventScenario.Pack(["tailwind"], EventScenario.Deck(EventKind.Good, null, "tailwind")), "Ивенты"));

        Assert.Equal(RejectionCodes.ContentInvalid, s.Last.Rejection?.Code);
        var paths = ContentPublishing.Check(EventScenario.Pack(["tailwind"], EventScenario.Deck(EventKind.Good, null, "tailwind"))).Select(e => e.Path);
        Assert.Contains("$.objects[0].kind", paths);
        Assert.Contains("$.wheels[0].deck", paths);
    }

    [Theory]
    [InlineData("deckOfItems", "$.wheels[0].entries[0].objectId")]
    [InlineData("eventInLootWheel", "$.wheels[0].entries[0].objectId")]
    [InlineData("standardOfOtherKind", "$.wheels[0].deck")]
    [InlineData("unknownDeck", "$.objects[1].effect.actions[0].deck")]
    [InlineData("spinDeck", "$.objects[1].effect.actions[0].wheel")]
    [InlineData("eventDrawsEvent", "$.objects[1].effect.actions[0]")]
    public void A_pack_with_events_checks_its_decks(string what, string path)
    {
        var tailwind = EconomyScenario.Examples()["tailwind"];
        var orange = EconomyScenario.Examples()["orange"];
        EffectSpec Acts(ActionSpec action) => new() { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [action] };
        var lucky = EventScenario.Deck(EventKind.Good, "lucky", "tailwind");
        var pack = what switch
        {
            "deckOfItems" => new ContentPack { Objects = [orange, tailwind], Wheels = [EventScenario.Deck(EventKind.Good, "lucky", "orange")] },
            "eventInLootWheel" => new ContentPack
            {
                Objects = [orange, tailwind],
                Wheels = [new WheelDefinition { Id = "box", Name = "Коробка", Entries = [new WheelEntrySpec { ObjectId = "tailwind", Weight = 1 }] }],
            },
            "standardOfOtherKind" => new ContentPack { Objects = [orange, tailwind], Wheels = [EventScenario.Deck(EventKind.Bad, "good", "tailwind")] },
            "unknownDeck" => new ContentPack { Objects = [orange, s_drawGood with { Effect = Acts(new DrawEventAction { Deck = "nowhere" }) }] },
            "spinDeck" => new ContentPack { Objects = [orange, s_drawGood with { Effect = Acts(new SpinWheelAction { Wheel = "lucky" }) }, tailwind], Wheels = [lucky] },
            _ => new ContentPack { Objects = [orange, tailwind with { Effect = Acts(new DrawEventAction { Deck = "good" }) }] },
        };

        Assert.Contains(path, ContentPublishing.Check(pack, events: true).Select(e => e.Path));
    }

    [Fact]
    public void A_pack_with_events_and_their_decks_is_accepted_with_events_on()
    {
        var pack = Pack(EventScenario.Deck(EventKind.Good, null, "tailwind"), EventScenario.Deck(EventKind.Risky, "swamp", "gamble", "horror-week"));

        Assert.Empty(ContentPublishing.Check(pack with { Objects = [.. pack.Objects, s_drawGood with { Effect = s_drawGood.Effect! with { Actions = [new DrawEventAction { Deck = "swamp" }] } }] }, events: true));
        Assert.Empty(ContentPublishing.Check(pack with { Objects = [.. pack.Objects, s_drawGood with { Effect = s_drawGood.Effect! with { Actions = [new DrawEventAction { Deck = DeckRef.Zone }] } }] }, events: true));
    }

    [Fact]
    public void The_events_and_decks_of_the_repository_publish_with_the_items()
    {
        var pack = EventScenario.RepositoryPack();

        Assert.Empty(ContentPublishing.Check(pack, events: true));
        Assert.All(["good", "bad", "risky"], d => Assert.Contains(pack.Wheels, w => w.Id == d && w.Deck is not null));
        Assert.Empty(ContentPublishing.Check(EconomyScenario.RepositoryPack()));
    }

    [Fact]
    public void The_first_finisher_draws_nothing()
    {
        // D-510: his effects are frozen; a short linear season, three d4 of 4 from the start reach the finish
        var s = EventScenario.New().WithMapLength(3).WithCategory("Horror").WithGame("Silent Hill", 9, "Horror").WithPlayers("Вася", "Петя");
        s.WithContent(Pack(EventScenario.Deck(EventKind.Good, null, "tailwind")));
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(4, 4, 4).Complete("Вася");
        Assert.NotNull(s.Player("Вася").Finish);

        Assert.Empty(EventDraws.Draw(s.State, s.Context(), s.PlayerId("Вася"), "good", EventSource.Item));
        Assert.Single(EventDraws.Draw(s.State, s.Context(), s.PlayerId("Петя"), "good", EventSource.Item).OfType<EventDrawn>());
    }

    [Fact]
    public void The_stop_of_a_drop_penalty_on_an_event_cell_draws()
    {
        // SPEC «Движение»: any stop; the penalty of two d4 of 1 walks back from b onto e
        var map = MapBuilder.New().EventCell("e", "good").Path("start", "a", "e", "x", "b", "c", "d", "f", "finish").Build();
        var s = To(Season(Pack(EventScenario.Deck(EventKind.Good, null, "tailwind")), map), "b");
        s.RollTitle("Вася", "Silent Hill").Start("Вася").Advance(TimeSpan.FromMinutes(61));

        s.NextRandom(1, 1, 0).Act(new Engine.Runs.DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal("e", s.Player("Вася").CellId);
        Assert.Equal(new EventDrawn(s.PlayerId("Вася"), "good", "tailwind", EventKind.Good, EventSource.Cell), Assert.Single(s.LastEvents<EventDrawn>()));
    }

    [Fact]
    public void An_event_cell_does_not_draw_once_events_are_switched_off_mid_season()
    {
        var s = To(Season(Pack(EventScenario.Deck(EventKind.Bad, "lucky", "tailwind"))), "a");
        s.WithRuleset(r => r with { Features = r.Features with { Events = false } });
        s.Give("Вася", "orange");

        s.Used("Вася", "orange");

        Assert.Equal("e", s.Player("Вася").CellId);
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
    }

    [Theory]
    [InlineData("given", "$.objects[1].effect.actions[0].objectId")]
    [InlineData("transformedInto", "$.objects[1].effect.actions[0].into")]
    [InlineData("manual", "$.objects[0].manual")]
    [InlineData("hostile", "$.objects[0].hostile")]
    [InlineData("condition", "$.objects[0].effect.condition")]
    [InlineData("target", "$.objects[0].effect.target")]
    public void An_event_is_played_on_its_drawer_and_never_kept(string what, string path)
    {
        var tailwind = EconomyScenario.Examples()["tailwind"];
        EffectSpec Acts(ActionSpec action) => new() { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [action] };
        ObjectDefinition[] objects = what switch
        {
            "given" => [tailwind, s_drawGood with { Effect = Acts(new GiveObjectAction { ObjectId = "tailwind" }) }],
            "transformedInto" => [tailwind, s_drawGood with { Effect = Acts(new TransformObjectAction { Mode = TransformMode.Transform, Into = "tailwind" }) }],
            "manual" => [EconomyScenario.Examples()["karaoke"]],
            "hostile" => [tailwind with { Hostile = true }],
            "condition" => [tailwind with { Effect = tailwind.Effect! with { Condition = new ConditionSpec { DifficultyAtLeast = Engine.Runs.Difficulty.Hard } } }],
            _ => [tailwind with { Effect = tailwind.Effect! with { Target = new TargetSpec { Selector = TargetSelector.Leader } } }],
        };

        Assert.Contains(path, ContentPublishing.Check(new ContentPack { Objects = [.. objects] }, events: true).Select(e => e.Path));
    }

    [Fact]
    public void A_loot_wheel_named_like_a_deck_still_publishes_while_events_are_off()
    {
        var orange = EconomyScenario.Examples()["orange"];
        var wheel = new WheelDefinition { Id = "good", Name = "Хорошее", Entries = [new WheelEntrySpec { ObjectId = "orange", Weight = 1 }] };

        Assert.Empty(ContentPublishing.Check(new ContentPack { Objects = [orange], Wheels = [wheel] }));
        Assert.NotEmpty(ContentPublishing.Check(new ContentPack { Objects = [orange], Wheels = [wheel] }, events: true));
    }

    [Theory]
    [InlineData(EventKind.Special)]
    [InlineData(EventKind.Risky)]
    public void A_difficulty_grants_a_special_or_risky_event_only_with_events_on(EventKind kind)
    {
        var ruleset = TestRuleset.Create();
        var granting = ruleset with
        {
            Reward = ruleset.Reward with
            {
                DieByDifficulty = ruleset.Reward.DieByDifficulty with { Hard = ruleset.Reward.DieByDifficulty.Hard with { GrantEvent = kind } },
            },
        };

        Assert.Contains(RulesetValidator.Validate(granting), e => e.Path == "reward.dieByDifficulty.hard.grantEvent");
        Assert.DoesNotContain(RulesetValidator.Validate(granting.WithEvents()), e => e.Path == "reward.dieByDifficulty.hard.grantEvent");
    }

    [Fact]
    public void Events_on_need_items_and_the_rules_of_events()
    {
        var ruleset = TestRuleset.Create();
        var on = ruleset with { Features = ruleset.Features with { Events = true } };

        Assert.Contains(RulesetSupport.Unsupported(on), e => e.Path == "features.events");
        Assert.Contains(RulesetValidator.Validate(on), e => e.Path == "events");
        Assert.Empty(RulesetSupport.Unsupported(ruleset.WithEvents()));
        Assert.Empty(RulesetValidator.Validate(ruleset.WithEvents()));
    }
}
