using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Events;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Decks;

/// <summary>
/// The Бабл (imported item «Бабл», effect <c>bubble-effect</c> with <c>intercept: ["badEvent", "hostile"]</c>; D-484 →
/// D-529 (3), option (а) as the expert panel decided): one charge, spent on the first threat that matches — a bad event
/// that lands on the player by chance (cell, item, an event drawn onto him by another; a difficulty grants only good events,
/// D-546 (5)) or a hostile effect. The bad event of a paid reroll and the mandatory bad event of a drop are a price and a
/// penalty: never intercepted, the charge stays. An intercepted event is drawn (<c>event-drawn</c>) and does not happen: <c>bad-event-intercepted(PlayerId,
/// InstanceId, ObjectId, Source, EventId)</c>, none of its actions. A targeted bad event from another player is one threat:
/// one charge, the oldest interceptor first. Behind <c>features.events</c>; undo returns the charge (panel (6)).
/// </summary>
public class BadEventInterceptTests
{
    private static readonly string[] s_objects =
        ["babl", "bubble-effect", "horror-week", "forced-genre", "tailwind", "sack-bad", "jinx", "orange", "bird-thief", "shield", "shield-effect"];

    /// <summary>start → c1 (event, bad) → c2 → … → finish; «Хоррор-неделя» is the bad deck, «Попутный ветер» the good one.</summary>
    private static Scenario Season(Func<Ruleset, Ruleset>? rules = null, string badEvent = "horror-week") =>
        EventSetup.Season(
            EventSetup.Pack([.. s_objects, badEvent], EventSetup.Deck(EventKind.Bad, badEvent), EventSetup.Deck(EventKind.Good, "tailwind")),
            EventSetup.Line(("c1", "bad"), ("c3", "good")),
            rules);

    private static BadEventIntercepted Intercepted(Scenario s) => Assert.Single(s.LastEvents<BadEventIntercepted>());

    private static void Spent(Scenario s, string player, Guid bubble) =>
        Assert.DoesNotContain(s.Player(player).Wallet.Inventory, o => o.InstanceId == bubble);

    private static void Kept(Scenario s, string player, Guid bubble) =>
        Assert.Contains(s.Player(player).Wallet.Inventory, o => o.InstanceId == bubble);

    [Fact]
    public void The_bubble_of_the_repository_is_the_imported_one_and_publishes_with_events()
    {
        // «Бабл импортирован» (task 5.3): the item and its effect are content files of the repository
        var pack = EventScenario.RepositoryPack();

        Assert.Equal(EventSetup.Object("babl"), Assert.Single(pack.Objects, o => o.Id == "babl"));
        Assert.Equal(EventSetup.Object("bubble-effect"), Assert.Single(pack.Objects, o => o.Id == "bubble-effect"));
        Assert.Empty(ContentPublishing.Check(pack, events: true));
    }

    // ---- What it stops ----

    [Fact]
    public void The_bubble_stops_a_bad_event_of_a_cell()
    {
        var s = Season();
        var bubble = s.Bubbled("Вася");

        s.Give("Вася", "orange");
        s.NextRandom(0).Used("Вася", "orange");

        var vasya = s.PlayerId("Вася");
        Assert.Equal(new EventDrawn(vasya, "bad", "horror-week", EventKind.Bad, EventSource.Cell), Assert.Single(s.LastEvents<EventDrawn>()));
        var stopped = Intercepted(s);
        Assert.Equal((vasya, bubble, "bubble-effect", EventSource.Cell, "horror-week"), (stopped.PlayerId, stopped.InstanceId, stopped.ObjectId, stopped.Source, stopped.EventId));
        Assert.True(s.IndexOf(e => e is EventDrawn) < s.IndexOf(e => e is BadEventIntercepted));

        // The event did not happen: no forced genre; the Бабл is spent
        Assert.DoesNotContain(s.LastEvents<ObjectGiven>(), g => g.Item.ObjectId == "forced-genre");
        Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));
        Spent(s, "Вася", bubble);
    }

    [Fact]
    public void The_bubble_stops_a_bad_event_of_an_item()
    {
        var s = Season();
        var bubble = s.Bubbled("Вася");
        s.Give("Вася", "sack-bad");

        s.NextRandom(0).Used("Вася", "sack-bad");

        Assert.Equal(EventSource.Item, Intercepted(s).Source);
        Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));
        Spent(s, "Вася", bubble);
    }

    [Fact]
    public void The_bubble_stops_a_bad_event_drawn_onto_its_owner_by_another_with_one_charge()
    {
        var s = Season();
        var bubble = s.Bubbled("Петя");
        s.Give("Вася", "jinx");

        s.NextRandom(0).Used("Вася", "jinx", target: "Петя");

        // One threat, one interception, whichever form it takes; the jinx is spent, nothing reaches Петя
        Assert.Equal(1, s.LastEvents<BadEventIntercepted>().Count() + s.LastEvents<HostileIntercepted>().Count());
        Assert.All(s.LastEvents<BadEventIntercepted>(), i => Assert.Equal((bubble, EventSource.Incoming), (i.InstanceId, i.Source)));
        Assert.All(s.LastEvents<HostileIntercepted>(), i => Assert.Equal(bubble, i.InstanceId));
        Assert.DoesNotContain("forced-genre", s.Inventory("Петя"));
        Assert.DoesNotContain("jinx", s.Inventory("Вася"));
        Spent(s, "Петя", bubble);
    }

    [Fact]
    public void The_bubble_stops_a_manual_bad_event_and_no_manual_effect_waits()
    {
        var s = Season(badEvent: "karaoke");
        var bubble = s.Bubbled("Вася");
        s.Give("Вася", "sack-bad");

        s.NextRandom(0).Used("Вася", "sack-bad");

        Assert.Equal("karaoke", Intercepted(s).EventId);
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
        Assert.Empty(s.State.ManualEffects);
        Spent(s, "Вася", bubble);
    }

    [Fact]
    public void The_bubble_stops_a_bad_event_a_difficulty_grants()
    {
        // The default ruleset grants only a good event (D-546 (5)); a ruleset may grant a bad one, which falls on the player
        // by chance, not as a price or a penalty, so the Бабл stops it (panel D-484 (1), D-557)
        var s = Season(r => r with
        {
            Reward = r.Reward with
            {
                DieByDifficulty = r.Reward.DieByDifficulty with { Extreme = r.Reward.DieByDifficulty.Extreme with { GrantEvent = EventKind.Bad } },
            },
        });
        var bubble = s.Bubbled("Вася");
        s.RollTitle("Вася", "Alan Wake").Start("Вася");

        s.NextRandom(2, 3, 0).Complete("Вася", Difficulty.Extreme);

        ScenarioAssert.Accepted(s);
        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal((EventKind.Bad, EventSource.Difficulty, "horror-week"), (drawn.Kind, drawn.Source, drawn.ObjectId));
        var stopped = Intercepted(s);
        Assert.Equal((bubble, EventSource.Difficulty, "horror-week"), (stopped.InstanceId, stopped.Source, stopped.EventId));
        Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));
        Spent(s, "Вася", bubble);
    }

    [Fact]
    public void With_events_switched_off_the_bubble_still_stops_a_hostile_effect_as_its_text_says()
    {
        // «Гасит следующий плохой ивент или враждебный эффект»: its `hostile` is the interception of stage 4 and works without
        // events; only `badEvent` waits for them, there being no bad event to stop (D-555, D-557)
        var s = Season().WithCoins("Вася", 10);
        var bubble = s.Bubbled("Вася");
        s.WithRuleset(r => r with { Features = r.Features with { Events = false } });
        s.Give("Петя", "bird-thief");

        s.Used("Петя", "bird-thief", target: "Вася");

        Assert.Equal(bubble, Assert.Single(s.LastEvents<HostileIntercepted>()).InstanceId);
        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Assert.Equal(10, s.Player("Вася").Coins);
        Spent(s, "Вася", bubble);
    }

    [Fact]
    public void The_bubble_stops_a_hostile_effect_first_and_then_the_bad_event_goes_through()
    {
        // «следующий плохой ивент или враждебный эффект»: whichever comes first takes the one charge
        var s = Season().WithCoins("Вася", 10);
        var bubble = s.Bubbled("Вася");
        s.Give("Петя", "bird-thief");
        s.Used("Петя", "bird-thief", target: "Вася");
        Assert.Equal(bubble, Assert.Single(s.LastEvents<HostileIntercepted>()).InstanceId);
        Assert.Equal(10, s.Player("Вася").Coins);
        Spent(s, "Вася", bubble);

        s.Give("Вася", "orange");
        s.NextRandom(0).Used("Вася", "orange");

        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Assert.Contains("forced-genre", s.Inventory("Вася"));
    }

    [Fact]
    public void One_bubble_stops_one_bad_event_the_second_goes_through()
    {
        var s = Season();
        s.Bubbled("Вася");
        s.Give("Вася", "sack-bad");
        s.Give("Вася", "sack-bad");
        s.NextRandom(0).Used("Вася", "sack-bad");
        Intercepted(s);

        s.NextRandom(0).Used("Вася", "sack-bad");

        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Assert.Contains("forced-genre", s.Inventory("Вася"));
    }

    // ---- What it never stops: the price of a reroll, the penalty of a drop ----

    [Fact]
    public void The_bubble_does_not_stop_the_bad_event_of_a_paid_reroll_and_keeps_its_charge()
    {
        var s = Season(r => r with { Roll = r.Roll with { FreeRerollsPerRoll = 0, RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } });
        var bubble = s.Bubbled("Вася");
        s.Roll("Вася");

        s.NextRandom(0).Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(EventSource.RerollCost, Assert.Single(s.LastEvents<EventDrawn>()).Source);
        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Assert.Contains(s.LastEvents<ObjectGiven>(), g => g.Item.ObjectId == "forced-genre");
        Kept(s, "Вася", bubble);
    }

    [Fact]
    public void The_bubble_does_not_stop_the_bad_event_of_a_drop_and_keeps_its_charge()
    {
        var s = Season();
        var bubble = s.Bubbled("Вася");
        s.At("Вася", "c8");
        s.PlayingFor("Вася");

        s.Dropped("Вася", 2, 1, 0);

        Assert.Equal(EventSource.DropPenalty, Assert.Single(s.LastEvents<EventDrawn>()).Source);
        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Assert.Contains("forced-genre", s.Inventory("Вася"));
        Kept(s, "Вася", bubble);
    }

    [Fact]
    public void The_bubble_lets_a_good_event_through_and_keeps_its_charge()
    {
        var s = Season();
        var bubble = s.Bubbled("Вася");
        s.At("Вася", "c2");

        s.Give("Вася", "orange");
        s.NextRandom(0).Used("Вася", "orange");

        Assert.Equal(EventKind.Good, Assert.Single(s.LastEvents<EventDrawn>()).Kind);
        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Assert.Single(s.Player("Вася").Wallet.NextDice);
        Kept(s, "Вася", bubble);
    }

    // ---- Several interceptors: the oldest first, one charge ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Of_a_shield_and_a_bubble_the_older_takes_a_hostile_bad_event_and_the_other_stays(bool shieldFirst)
    {
        var s = Season();
        Guid shield, bubble;
        if (shieldFirst)
        {
            s.Give("Петя", "shield");
            s.Used("Петя", "shield");
            shield = s.Held("Петя", "shield-effect").InstanceId;
            bubble = s.Bubbled("Петя");
        }
        else
        {
            bubble = s.Bubbled("Петя");
            s.Give("Петя", "shield");
            s.Used("Петя", "shield");
            shield = s.Held("Петя", "shield-effect").InstanceId;
        }

        s.Give("Вася", "jinx");

        s.NextRandom(0).Used("Вася", "jinx", target: "Петя");

        var used = s.LastEvents<HostileIntercepted>().Select(i => i.InstanceId).Concat(s.LastEvents<BadEventIntercepted>().Select(i => i.InstanceId)).ToList();
        Assert.Equal([shieldFirst ? shield : bubble], used);
        Kept(s, "Петя", shieldFirst ? bubble : shield);
        Assert.DoesNotContain("forced-genre", s.Inventory("Петя"));
    }

    // ---- Undo, events off, replay ----

    [Fact]
    public void Undoing_an_interception_gives_the_charge_back()
    {
        var s = Season();
        var bubble = s.Bubbled("Вася");
        s.Give("Вася", "sack-bad");
        s.NextRandom(0).Used("Вася", "sack-bad");
        Intercepted(s);

        s.Act(new UndoCommand(s.LastCommandId, "Не тот мешок"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonEngine.Replay(s.EffectiveLog).Players, s.State.Players);
        Kept(s, "Вася", bubble);
        Assert.Contains("sack-bad", s.Inventory("Вася"));
    }

    [Fact]
    public void With_events_switched_off_the_bubble_stops_no_manual_bad_event()
    {
        var s = Season();
        var bubble = s.Bubbled("Вася");
        s.WithRuleset(r => r with { Features = r.Features with { Events = false } });
        s.PlayingFor("Вася");

        s.Dropped("Вася", 2, 1);

        Assert.Equal(ManualEffectSource.Drop, Assert.Single(s.LastEvents<ManualEffectCreated>()).Source);
        Assert.Empty(s.LastEvents<BadEventIntercepted>());
        Kept(s, "Вася", bubble);
    }

    [Fact]
    public void A_replay_of_an_interception_gives_the_same_state()
    {
        var s = Season();
        s.Bubbled("Вася");
        s.Give("Вася", "sack-bad");
        s.NextRandom(0).Used("Вася", "sack-bad");

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void The_bad_event_intercepted_event_has_its_name_in_the_log()
    {
        Assert.Equal("bad-event-intercepted", EventCatalog.Describe(typeof(BadEventIntercepted)).Name);
    }
}
