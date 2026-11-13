using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Task 5.4 «Накопление и порядок» (SPEC «Ивенты» → «Накопление»: events accumulate, one that does not fit the current game
/// waits for the next fitting one, the order is special, good, bad; D-505, D-543; EV7, EV8, EV60). The waiting changes of
/// the next roll and the next throw carry the kind of the event that gave them (<c>eventKind</c>, only on events); a roll
/// from Idle and a throw apply them by <c>events.applyOrder</c> (default special, good, bad; an unlisted risky after the
/// listed), then the changes of items, within a kind by receipt. An event filter that would leave the roll empty is not
/// applied and waits (Н-6); item filters behave as in stage 4.
/// <para>
/// The pool: categories Horror (Silent Hill 3 h, tags Horror and Classic; Fatal Frame 3 h) and RPG (Gothic 3 h, Witcher 6 h).
/// Every deck (special, good, bad, risky) holds all the test events in <see cref="s_deck"/> order, one ticket each, so the kind
/// of a drawn event is the kind of the deck it was drawn from and a draw is scripted by the event's index.
/// </para>
/// </summary>
public partial class AccumulationTests
{
    /// <summary>The events of every deck, in deck order (the ticket of a draw is the index).</summary>
    private static readonly string[] s_deck =
    [
        "horror-filter", "rpg-filter", "short-filter", "classic-filter", "rpg-week", "horror-week",
        "dice-count", "dice-add", "dice-double", "bad-ask", "special-ask", "cond-hard", "cond-easy",
    ];

    private static readonly Dictionary<string, string> s_json = new(StringComparer.Ordinal)
    {
        ["horror-filter"] = Event("horror-filter", "Только хоррор", """[{ "type": "modifyNextRoll", "filter": { "tags": ["Horror"] } }]"""),
        ["rpg-filter"] = Event("rpg-filter", "Только RPG", """[{ "type": "modifyNextRoll", "filter": { "tags": ["RPG"] } }]"""),
        ["short-filter"] = Event("short-filter", "Короче четырёх часов", """[{ "type": "modifyNextRoll", "filter": { "maxHours": 4 } }]"""),
        ["classic-filter"] = Event("classic-filter", "Только классика", """[{ "type": "modifyNextRoll", "filter": { "tags": ["Classic"] } }]"""),
        ["rpg-week"] = Event("rpg-week", "RPG-неделя", """[{ "type": "giveObject", "objectId": "forced-genre", "params": { "tag": "RPG" } }]"""),
        ["dice-count"] = Event("dice-count", "Лишний кубик", """[{ "type": "modifyDice", "when": "next", "stage": "count", "value": 1 }]"""),
        ["dice-add"] = Event("dice-add", "Плюс два", """[{ "type": "modifyDice", "when": "next", "stage": "add", "value": 2 }]"""),
        ["dice-double"] = Event("dice-double", "Вдвойне", """[{ "type": "modifyDice", "when": "next", "stage": "multiply", "value": 2 }]"""),
        ["cond-hard"] = Event("cond-hard", "Не ниже сложной", """[{ "type": "modifyNextRoll", "runCondition": { "difficultyAtLeast": "hard" } }]"""),
        ["cond-easy"] = Event("cond-easy", "Не выше лёгкой", """[{ "type": "modifyNextRoll", "runCondition": { "difficultyAtMost": "easy" } }]"""),
        ["bad-ask"] = Asking("bad-ask", "x", "y"),
        ["special-ask"] = Asking("special-ask", "p", "q"),
        ["bad-then-special"] = """
            {
              "id": "bad-then-special", "kind": "item", "name": "Два мешка", "description": "Вытяни плохой, потом специвент.",
              "rarity": "common", "price": 10, "window": "anytime", "hostile": false, "stackable": true,
              "effect": { "target": { "selector": "self" }, "actions": [{ "type": "drawEvent", "deck": "bad" }, { "type": "drawEvent", "deck": "special" }] }
            }
            """,
    };

    private static string Event(string id, string name, string actions) => $$"""
        {
          "id": "{{id}}", "kind": "event", "name": "{{name}}", "description": "{{name}}.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": {{actions}} }
        }
        """;

    private static string Asking(string id, string first, string second) => $$"""
        {
          "id": "{{id}}", "kind": "event", "name": "{{id}}", "description": "Вопрос.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": {
            "target": { "selector": "self" },
            "outcomes": {
              "choice": { "prompt": "Как?", "options": { "list": ["{{first}}", "{{second}}"] } },
              "cases": [
                { "option": "{{first}}", "actions": [{ "type": "changeResource", "resource": "coins", "amount": 1 }] },
                { "option": "{{second}}", "actions": [{ "type": "changeResource", "resource": "coins", "amount": 2 }] }
              ]
            }
          }
        }
        """;

    private static readonly string[] s_objects =
    [
        .. s_deck, "bad-then-special", "forced-genre", "sack-special", "sack-good", "sack-bad", "sack-risky", "jinx",
        "short-game", "pick-of-three", "lucky-die", "dirty-trick",
    ];

    private static ObjectDefinition Object(string id)
    {
        if (s_json.TryGetValue(id, out var json))
        {
            return ContentJson.Parse<ObjectDefinition>(json);
        }

        return id == "dirty-trick" ? EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == id) : EventSetup.Object(id);
    }

    internal static ContentPack Pack() => new()
    {
        Objects = [.. s_objects.Distinct(StringComparer.Ordinal).Select(Object).OrderBy(o => o.Id, StringComparer.Ordinal)],
        Wheels =
        [
            .. new[] { EventKind.Bad, EventKind.Good, EventKind.Risky, EventKind.Special }
                .Select(k => EventSetup.Deck(k, s_deck))
                .OrderBy(d => d.Id, StringComparer.Ordinal),
        ],
    };

    /// <summary>start → c1 … c40 → finish: no event cells, long enough that two completions never reach the finish.</summary>
    internal static MapGraph LongLine() =>
        MapBuilder.New().Path(["start", .. Enumerable.Range(1, 40).Select(i => $"c{i}"), "finish"]).Build();

    /// <summary>The season of these tests; <paramref name="horror"/> false — no horror game in the pool (the category is there).</summary>
    private static Scenario Season(Func<Ruleset, Ruleset>? rules = null, bool horror = true)
    {
        var ruleset = TestRuleset.Create().WithEvents();
        var s = Scenario.New(rules is null ? ruleset : rules(ruleset)).WithMap(LongLine())
            .WithCategory("Horror").WithCategory("RPG");
        if (horror)
        {
            s.WithGame("Silent Hill", 3, "Horror", "Classic").WithGame("Fatal Frame", 3, "Horror");
        }

        s.WithGame("Gothic", 3, "RPG").WithGame("Witcher", 6, "RPG").WithPlayers("Вася", "Петя");
        return s.WithContent(Pack());
    }

    /// <summary>A season whose only game is Silent Hill (3 hours: one d4 at normal), so a throw is fully scripted.</summary>
    private static Scenario DiceSeason(Func<Ruleset, Ruleset>? rules = null)
    {
        var ruleset = TestRuleset.Create().WithEvents();
        var s = Scenario.New(rules is null ? ruleset : rules(ruleset)).WithMap(LongLine())
            .WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася", "Петя");
        s.WithContent(Pack());
        s.Roll("Вася").Start("Вася");
        return s;
    }

    private static Func<Ruleset, Ruleset> Order(params EventKind[] kinds) =>
        r => r with { Events = r.Events! with { ApplyOrder = new EquatableArray<EventKind>(kinds) } };

    /// <summary>The player draws <paramref name="eventId"/> from the deck of <paramref name="kind"/> with a sack of that kind.</summary>
    private static Scenario Draw(Scenario s, string player, EventKind kind, string eventId)
    {
        var sack = $"sack-{kind.ToString().ToLowerInvariant()}";
        s.Give(player, sack);
        s.NextRandom(Array.IndexOf(s_deck, eventId)).Used(player, sack);
        Assert.Equal(new[] { (eventId, kind) }, s.LastEvents<EventDrawn>().Select(d => (d.ObjectId, d.Kind)));
        Assert.Equal(0, s.Random.ScriptedLeft);
        return s;
    }

    private static Scenario UseItem(Scenario s, string player, string item, string? target = null, params string[] choices)
    {
        s.Give(player, item);
        return s.Used(player, item, target, choices);
    }

    private static RollModifiersApplied Applied(Scenario s) => Assert.Single(s.LastEvents<RollModifiersApplied>());

    private static IEnumerable<(string, EventKind?)> Kinds(IEnumerable<RollModifier> modifiers) => modifiers.Select(m => (m.ObjectId, m.EventKind));

    private static EquatableArray<string> Tags(Scenario s, string player) => s.PoolGame(s.Player(player).Offer!.GameId).Tags;

    /// <summary>The player starts the game offered and completes it at normal (the dice from the seed).</summary>
    private static Scenario Plays(Scenario s, string player)
    {
        s.Start(player).Complete(player);
        ScenarioAssert.Accepted(s);
        return s;
    }

    // ---- EV8, S5.4-03: the throw, special, good, bad ----

    [Fact]
    public void Special_then_good_then_bad()
    {
        // Received bad (×2), good (+2), special (+1 die); the throw takes them special, good, bad, the pipeline keeps its
        // stages (count → roll → add → multiply): (1 + 1 + 2) × 2 = 8
        var s = DiceSeason();
        Draw(s, "Вася", EventKind.Bad, "dice-double");
        Draw(s, "Вася", EventKind.Good, "dice-add");
        Draw(s, "Вася", EventKind.Special, "dice-count");
        Assert.Equal(
            new EventKind?[] { EventKind.Bad, EventKind.Good, EventKind.Special },
            s.Player("Вася").Wallet.NextDice.Select(d => d.EventKind));

        s.NextRandom(1, 1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var modified = Assert.Single(s.LastEvents<RunDiceModified>());
        Assert.Equal(new[] { "dice-count", "dice-add", "dice-double" }, modified.Mods.Sources);
        Assert.Equal(3, modified.SpentNext);
        Assert.Equal((8, "c8"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Empty(s.Player("Вася").Wallet.NextDice);
    }

    [Fact]
    public void Order_comes_from_the_ruleset()
    {
        // S5.4-08, EV60: events.applyOrder [bad, good, special] reverses the order; the pipeline's stages and the total do not change
        var s = DiceSeason(Order(EventKind.Bad, EventKind.Good, EventKind.Special));
        Draw(s, "Вася", EventKind.Special, "dice-count");
        Draw(s, "Вася", EventKind.Good, "dice-add");
        Draw(s, "Вася", EventKind.Bad, "dice-double");

        s.NextRandom(1, 1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var modified = Assert.Single(s.LastEvents<RunDiceModified>());
        Assert.Equal(new[] { "dice-double", "dice-add", "dice-count" }, modified.Mods.Sources);
        Assert.Equal((8, "c8"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Waiting_dice_changes_of_events_come_before_those_of_items()
    {
        // The item's +1d6 came first, the bad event's ×2 later: the event goes first; (1 + 1) × 2 = 4
        var s = DiceSeason();
        UseItem(s, "Вася", "lucky-die");
        Draw(s, "Вася", EventKind.Bad, "dice-double");
        Assert.Equal(new EventKind?[] { null, EventKind.Bad }, s.Player("Вася").Wallet.NextDice.Select(d => d.EventKind));

        s.NextRandom(1, 1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var modified = Assert.Single(s.LastEvents<RunDiceModified>());
        Assert.Equal(new[] { "dice-double", "lucky-die" }, modified.Mods.Sources);
        Assert.Equal(4, s.Player("Вася").Points);
    }

    [Fact]
    public void Waiting_dice_changes_of_one_kind_apply_in_the_order_received()
    {
        var s = DiceSeason();
        Draw(s, "Вася", EventKind.Good, "dice-double");
        Draw(s, "Вася", EventKind.Good, "dice-add");

        s.NextRandom(1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(new[] { "dice-double", "dice-add" }, Assert.Single(s.LastEvents<RunDiceModified>()).Mods.Sources);
        Assert.Equal(6, s.Player("Вася").Points);
    }

    // ---- EV8, D-505: the roll from Idle ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Roll_changes_apply_special_then_good_then_bad(bool explicitDefault)
    {
        // Received bad, good, special; the filters all fit Silent Hill. Without events.applyOrder the order is the
        // default special, good, bad, the same as the list written out
        var s = Season(explicitDefault ? Order(EventKind.Special, EventKind.Good, EventKind.Bad) : null);
        Draw(s, "Вася", EventKind.Bad, "horror-filter");
        Draw(s, "Вася", EventKind.Good, "short-filter");
        Draw(s, "Вася", EventKind.Special, "classic-filter");

        s.Roll("Вася");

        var applied = Applied(s);
        Assert.Equal(
            new (string, EventKind?)[] { ("classic-filter", EventKind.Special), ("short-filter", EventKind.Good), ("horror-filter", EventKind.Bad) },
            Kinds(applied.Modifiers));
        Assert.Empty(applied.Waiting);
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Вася").Offer!.GameId);
        Assert.Equal(applied.Modifiers, s.Player("Вася").Wallet.CurrentRoll);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
    }

    [Fact]
    public void A_ruleset_order_also_orders_roll_changes()
    {
        var s = Season(Order(EventKind.Bad, EventKind.Good, EventKind.Special));
        Draw(s, "Вася", EventKind.Special, "classic-filter");
        Draw(s, "Вася", EventKind.Good, "short-filter");
        Draw(s, "Вася", EventKind.Bad, "horror-filter");

        s.Roll("Вася");

        Assert.Equal(
            new (string, EventKind?)[] { ("horror-filter", EventKind.Bad), ("short-filter", EventKind.Good), ("classic-filter", EventKind.Special) },
            Kinds(Applied(s).Modifiers));
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Вася").Offer!.GameId);
    }

    [Fact]
    public void Event_roll_changes_come_before_item_ones_received_earlier()
    {
        // S5.4-04: the item's filter came first, the bad event's later; they do not meet in emptiness
        var s = Season();
        UseItem(s, "Вася", "short-game");
        Draw(s, "Вася", EventKind.Bad, "horror-filter");

        s.Roll("Вася");

        Assert.Equal(new (string, EventKind?)[] { ("horror-filter", EventKind.Bad), ("short-game", null) }, Kinds(Applied(s).Modifiers));
        Assert.Contains("Horror", Tags(s, "Вася"));
    }

    [Fact]
    public void An_unlisted_risky_change_goes_after_the_listed_kinds_and_before_items()
    {
        var s = Season();
        UseItem(s, "Вася", "short-game");
        Draw(s, "Вася", EventKind.Risky, "classic-filter");
        Draw(s, "Вася", EventKind.Bad, "horror-filter");

        s.Roll("Вася");

        Assert.Equal(
            new (string, EventKind?)[] { ("horror-filter", EventKind.Bad), ("classic-filter", EventKind.Risky), ("short-game", null) },
            Kinds(Applied(s).Modifiers));
    }

    [Fact]
    public void A_listed_risky_change_goes_where_the_ruleset_puts_it()
    {
        var s = Season(Order(EventKind.Risky, EventKind.Special, EventKind.Good, EventKind.Bad));
        Draw(s, "Вася", EventKind.Bad, "horror-filter");
        Draw(s, "Вася", EventKind.Special, "short-filter");
        Draw(s, "Вася", EventKind.Risky, "classic-filter");

        s.Roll("Вася");

        Assert.Equal(
            new (string, EventKind?)[] { ("classic-filter", EventKind.Risky), ("short-filter", EventKind.Special), ("horror-filter", EventKind.Bad) },
            Kinds(Applied(s).Modifiers));
    }

    // ---- EV8, D-543: special rolls given by events ----

    [Fact]
    public void Two_imposed_genres_of_different_kinds_apply_in_order()
    {
        // «Хоррор-неделя» (bad) came first, «RPG-неделя» (special) later: the special one fires at this roll, the bad one
        // waits for the next
        var s = Season();
        Draw(s, "Вася", EventKind.Bad, "horror-week");
        Draw(s, "Вася", EventKind.Special, "rpg-week");
        Assert.Equal(
            new EventKind?[] { EventKind.Bad, EventKind.Special },
            s.Player("Вася").Wallet.Inventory.Where(o => o.ObjectId == "forced-genre").Select(o => o.EventKind));

        s.Roll("Вася");

        var first = Applied(s).Modifiers[0];
        Assert.Equal(("forced-genre", (EventKind?)EventKind.Special), (first.ObjectId, first.EventKind));
        Assert.Equal(new[] { "RPG" }, first.Filter!.Tags!.Value);
        Assert.Contains("RPG", Tags(s, "Вася"));
        Assert.Equal(EventKind.Bad, Assert.Single(s.Player("Вася").Wallet.Inventory, o => o.ObjectId == "forced-genre").EventKind);

        Plays(s, "Вася").Roll("Вася");

        var second = Applied(s).Modifiers[0];
        Assert.Equal(("forced-genre", (EventKind?)EventKind.Bad), (second.ObjectId, second.EventKind));
        Assert.Equal(new[] { "Horror" }, second.Filter!.Tags!.Value);
        Assert.Contains("Horror", Tags(s, "Вася"));
        Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));
    }

    [Fact]
    public void An_event_special_roll_fires_before_an_item_one_received_earlier()
    {
        // Вася's «Подлянка» gives Петя an imposed RPG (no kind); later Петя draws «Хоррор-неделя»: the event's fires first
        var s = Season().WithPoints("Петя", 3);
        UseItem(s, "Вася", "dirty-trick", "Петя", "RPG");
        Assert.Null(s.Held("Петя", "forced-genre").EventKind);
        Draw(s, "Петя", EventKind.Bad, "horror-week");

        s.Roll("Петя");

        var first = Applied(s).Modifiers[0];
        Assert.Equal(("forced-genre", (EventKind?)EventKind.Bad), (first.ObjectId, first.EventKind));
        Assert.Contains("Horror", Tags(s, "Петя"));
        Assert.Null(Assert.Single(s.Player("Петя").Wallet.Inventory, o => o.ObjectId == "forced-genre").EventKind);
    }

    [Fact]
    public void Within_a_kind_the_special_roll_fired_at_the_roll_goes_first()
    {
        // Both good: the filter came first, «Хоррор-неделя» from the good deck later; its special roll fires at this roll
        var s = Season();
        Draw(s, "Вася", EventKind.Good, "short-filter");
        Draw(s, "Вася", EventKind.Good, "horror-week");
        Assert.Equal(EventKind.Good, s.Held("Вася", "forced-genre").EventKind);

        s.Roll("Вася");

        Assert.Equal(new (string, EventKind?)[] { ("forced-genre", EventKind.Good), ("short-filter", EventKind.Good) }, Kinds(Applied(s).Modifiers));
        Assert.Contains("Horror", Tags(s, "Вася"));
    }

    // ---- EV7, S5.4-01: a change drawn after the roll waits for the next ----

    [Fact]
    public void A_roll_change_drawn_while_playing_waits_for_the_next_roll()
    {
        var s = Season();
        s.Roll("Вася").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        var game = s.State.Runs[runId].GameId;
        Draw(s, "Вася", EventKind.Bad, "horror-week");
        Assert.Equal(EventKind.Bad, s.Held("Вася", "forced-genre").EventKind);

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(game, s.State.Runs[runId].GameId);
        Assert.Empty(s.State.Runs[runId].Snapshot.ImposedTags);

        s.Roll("Вася");

        var applied = Assert.Single(Applied(s).Modifiers);
        Assert.Equal(("forced-genre", (EventKind?)EventKind.Bad), (applied.ObjectId, applied.EventKind));
        Assert.Contains("Horror", Tags(s, "Вася"));
    }

    // ---- EV7, S5.4-02, Н-6: an event filter with no fitting game waits for one ----

    [Fact]
    public void An_event_filter_with_no_fitting_game_does_not_block_the_roll_and_waits_for_a_fitting_one()
    {
        var s = Season(horror: false);
        Draw(s, "Вася", EventKind.Bad, "horror-filter");

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        var applied = Applied(s);
        Assert.DoesNotContain(applied.Modifiers, m => m.ObjectId == "horror-filter");
        Assert.Equal(new (string, EventKind?)[] { ("horror-filter", EventKind.Bad) }, Kinds(applied.Waiting));
        Assert.DoesNotContain("Horror", Tags(s, "Вася"));
        Assert.Equal(new (string, EventKind?)[] { ("horror-filter", EventKind.Bad) }, Kinds(s.Player("Вася").Wallet.NextRoll));

        // A horror game joins the pool: the next roll from Idle applies the waiting filter
        Plays(s, "Вася");
        s.WithGame("Silent Hill", 3, "Horror");
        s.Roll("Вася");

        Assert.Contains(Applied(s).Modifiers, m => m.ObjectId == "horror-filter" && m.EventKind == EventKind.Bad);
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Вася").Offer!.GameId);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
    }

    [Fact]
    public void An_event_imposed_genre_with_no_fitting_game_is_spent_only_when_it_applies()
    {
        var s = Season(horror: false);
        Draw(s, "Вася", EventKind.Bad, "horror-week");

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(Applied(s).Modifiers, m => m.ObjectId == "forced-genre");
        Assert.DoesNotContain("Horror", Tags(s, "Вася"));
        Assert.True(
            s.Inventory("Вася").Contains("forced-genre") || s.Player("Вася").Wallet.NextRoll.Any(m => m.ObjectId == "forced-genre"),
            "The imposed horror is lost though no roll took it.");

        Plays(s, "Вася");
        s.WithGame("Silent Hill", 3, "Horror");
        s.Roll("Вася");

        Assert.Contains(Applied(s).Modifiers, m => m.ObjectId == "forced-genre" && m.EventKind == EventKind.Bad);
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Вася").Offer!.GameId);
        Assert.DoesNotContain("forced-genre", s.Inventory("Вася"));
        Assert.DoesNotContain(s.Player("Вася").Wallet.NextRoll, m => m.ObjectId == "forced-genre");
    }

    [Fact]
    public void An_item_imposed_genre_with_no_fitting_game_still_leaves_the_roll_empty()
    {
        // Stage 4 unchanged with events on: the special roll of an item has no kind and is not dropped (D-92)
        var s = Season().WithPoints("Петя", 3).WithCategory("Racing");
        UseItem(s, "Вася", "dirty-trick", "Петя", "Racing");

        s.ExpectRejection().Roll("Петя");

        Assert.Equal(RejectionCodes.NoAvailableGames, s.Last.Rejection!.Code);
        Assert.Null(s.Held("Петя", "forced-genre").EventKind);
    }

    // ---- EV8, S5.4-05/06: meeting in emptiness, the higher one wins ----

    [Fact]
    public void Filters_meeting_in_emptiness_keep_the_higher_kind_and_the_other_waits()
    {
        // The bad «only horror» came first, the special «only RPG» later; no horror RPG in the pool
        var s = Season();
        Draw(s, "Вася", EventKind.Bad, "horror-filter");
        Draw(s, "Вася", EventKind.Special, "rpg-filter");

        s.Roll("Вася");

        var applied = Applied(s);
        Assert.Equal(new (string, EventKind?)[] { ("rpg-filter", EventKind.Special) }, Kinds(applied.Modifiers));
        Assert.Equal(new (string, EventKind?)[] { ("horror-filter", EventKind.Bad) }, Kinds(applied.Waiting));
        Assert.Contains("RPG", Tags(s, "Вася"));
        Assert.Equal(new[] { "horror-filter" }, s.Player("Вася").Wallet.NextRoll.Select(m => m.ObjectId));

        Plays(s, "Вася").Roll("Вася");

        Assert.Equal(new (string, EventKind?)[] { ("horror-filter", EventKind.Bad) }, Kinds(Applied(s).Modifiers));
        Assert.Contains("Horror", Tags(s, "Вася"));
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
    }

    [Fact]
    public void The_ruleset_order_decides_which_filter_wins_in_emptiness()
    {
        var s = Season(Order(EventKind.Bad, EventKind.Good, EventKind.Special));
        Draw(s, "Вася", EventKind.Special, "rpg-filter");
        Draw(s, "Вася", EventKind.Bad, "horror-filter");

        s.Roll("Вася");

        var applied = Applied(s);
        Assert.Equal(new[] { "horror-filter" }, applied.Modifiers.Select(m => m.ObjectId));
        Assert.Equal(new[] { "rpg-filter" }, applied.Waiting.Select(m => m.ObjectId));
        Assert.Contains("Horror", Tags(s, "Вася"));
    }

    [Theory]
    [InlineData("rpg-filter", "horror-filter", "RPG")]
    [InlineData("horror-filter", "rpg-filter", "Horror")]
    public void Within_a_kind_the_earlier_filter_wins_in_emptiness(string earlier, string later, string tag)
    {
        var s = Season();
        Draw(s, "Вася", EventKind.Good, earlier);
        Draw(s, "Вася", EventKind.Good, later);

        s.Roll("Вася");

        var applied = Applied(s);
        Assert.Equal(new[] { earlier }, applied.Modifiers.Select(m => m.ObjectId));
        Assert.Equal(new[] { later }, applied.Waiting.Select(m => m.ObjectId));
        Assert.Contains(tag, Tags(s, "Вася"));
        Assert.Equal(new[] { later }, s.Player("Вася").Wallet.NextRoll.Select(m => m.ObjectId));
    }

    // ---- EV17, EV8, S5.4-09: a change drawn during a roll does not join it ----

    [Fact]
    public void A_change_drawn_onto_a_player_choosing_a_game_waits_for_the_next_roll()
    {
        var s = Season();
        UseItem(s, "Вася", "pick-of-three").Roll("Вася");
        var choice = s.Player("Вася").Choice!;
        Assert.Equal(ChoiceKind.Game, choice.Kind);
        var current = s.Player("Вася").Wallet.CurrentRoll;
        s.Give("Петя", "jinx");

        s.NextRandom(Array.IndexOf(s_deck, "rpg-filter")).Used("Петя", "jinx", target: "Вася");

        Assert.Equal(choice, s.Player("Вася").Choice);
        Assert.Equal(current, s.Player("Вася").Wallet.CurrentRoll);
        Assert.Equal(new (string, EventKind?)[] { ("rpg-filter", EventKind.Bad) }, Kinds(s.Player("Вася").Wallet.NextRoll));

        s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id));
        ScenarioAssert.Accepted(s);
        Assert.Equal(choice.Options[0].Game!.GameId, s.State.Runs[s.Player("Вася").ActiveRunId!.Value].GameId);
        s.Complete("Вася");
        ScenarioAssert.Accepted(s);

        s.Roll("Вася");

        Assert.Contains(Applied(s).Modifiers, m => m.ObjectId == "rpg-filter" && m.EventKind == EventKind.Bad);
        Assert.Contains("RPG", Tags(s, "Вася"));
    }

    // ---- D-562, D-565: suspended questions follow events.applyOrder ----

    [Theory]
    [InlineData(false, "p")]
    [InlineData(true, "x")]
    public void Questions_of_suspended_events_are_asked_by_the_ruleset_order(bool badFirst, string firstOption)
    {
        var s = Season(badFirst ? Order(EventKind.Bad, EventKind.Good, EventKind.Special) : null);
        s.Give("Вася", "bad-then-special");

        s.NextRandom(Array.IndexOf(s_deck, "bad-ask"), Array.IndexOf(s_deck, "special-ask")).Used("Вася", "bad-then-special");

        Assert.Equal(new[] { "bad-ask", "special-ask" }, s.LastEvents<EffectSuspended>().Select(e => e.ObjectId));
        var choice = s.Player("Вася").Choice!;
        Assert.Equal(ChoiceKind.Effect, choice.Kind);
        Assert.Equal(firstOption, choice.Options[0].Id);

        s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, firstOption));

        ScenarioAssert.Accepted(s);
        Assert.Equal(badFirst ? "p" : "x", s.Player("Вася").Choice!.Options[0].Id);
    }

    // ---- What is written: the kind only on events (D-505, D-543, S5.4-07) ----

    [Fact]
    public void Changes_of_items_are_written_without_an_event_kind()
    {
        var s = Season();
        UseItem(s, "Вася", "short-game");
        var roll = Assert.Single(s.LastEvents<NextRollModified>());
        Assert.Null(roll.Modifier.EventKind);
        Assert.DoesNotContain("eventKind", EventCodec.Encode(roll).Data, StringComparison.Ordinal);

        s.Roll("Вася").Start("Вася");
        UseItem(s, "Вася", "lucky-die");
        var dice = Assert.Single(s.LastEvents<NextDiceModified>());
        Assert.Null(dice.Modifier.EventKind);
        Assert.DoesNotContain("eventKind", EventCodec.Encode(dice).Data, StringComparison.Ordinal);

        // The special roll given by an item: no kind on the object
        var t = Season().WithPoints("Петя", 3);
        UseItem(t, "Вася", "dirty-trick", "Петя", "RPG");
        var given = Assert.Single(t.LastEvents<ObjectGiven>(), g => g.Item.ObjectId == "forced-genre");
        Assert.Null(given.Item.EventKind);
        Assert.DoesNotContain("eventKind", EventCodec.Encode(given).Data, StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_and_objects_given_by_events_carry_the_kind_of_the_deck()
    {
        var s = Season();
        Draw(s, "Вася", EventKind.Good, "short-filter");
        var roll = Assert.Single(s.LastEvents<NextRollModified>());
        Assert.Equal(EventKind.Good, roll.Modifier.EventKind);
        Assert.Contains("\"eventKind\":\"good\"", EventCodec.Encode(roll).Data, StringComparison.Ordinal);
        Assert.Equal(roll, EventCodec.Decode(EventCodec.Encode(roll)));

        Draw(s, "Вася", EventKind.Bad, "horror-week");
        var given = Assert.Single(s.LastEvents<ObjectGiven>(), g => g.Item.ObjectId == "forced-genre");
        Assert.Equal(EventKind.Bad, given.Item.EventKind);
        Assert.Contains("\"eventKind\":\"bad\"", EventCodec.Encode(given).Data, StringComparison.Ordinal);

        s.Roll("Вася").Start("Вася");
        Draw(s, "Вася", EventKind.Special, "dice-add");
        var dice = Assert.Single(s.LastEvents<NextDiceModified>());
        Assert.Equal(EventKind.Special, dice.Modifier.EventKind);
        Assert.Contains("\"eventKind\":\"special\"", EventCodec.Encode(dice).Data, StringComparison.Ordinal);
        Assert.Equal(dice, EventCodec.Decode(EventCodec.Encode(dice)));
    }

    [Fact]
    public void A_stage_4_roll_change_is_stored_as_before()
    {
        // The frozen formats of stage 4 (Kernel/EventFormatTests.Economy): the new field is not written while null
        var player = SequentialIds.Make(0x10000000, 1);
        var roll = EventCodec.Encode(new NextRollModified(player, new RollModifier("short-game", new GameFilterSpec { MaxHours = 10 }, null)));
        var dice = EventCodec.Encode(new NextDiceModified(player, new DiceModifier("lucky-die", DiceStage.Add, ContentValue.TryParse("1d6")!)));

        Assert.Equal(
            """{"playerId":"10000000-0000-0000-0000-000000000001","modifier":{"objectId":"short-game","filter":{"tags":null,"maxHours":10,"minHours":null,"releaseYearBefore":null},"choiceCount":null,"hostile":false}}""",
            roll.Data);
        Assert.Equal(
            """{"playerId":"10000000-0000-0000-0000-000000000001","modifier":{"objectId":"lucky-die","stage":"add","value":"1d6","hostile":false}}""",
            dice.Data);
    }

    // ---- With events off nothing changes ----

    [Fact]
    public void With_events_off_an_order_in_the_rules_changes_nothing()
    {
        static Scenario Play(EventRules? events)
        {
            var s = Scenario.New(TestRuleset.Create().WithEconomy() with { Events = events })
                .WithCategory("Horror").WithCategory("RPG")
                .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Gothic", 3, "RPG").WithGame("Witcher", 6, "RPG")
                .WithPlayers("Вася");
            s.WithContent(EconomyScenario.ExamplePack("short-game", "pick-of-three"));
            UseItem(s, "Вася", "pick-of-three");
            UseItem(s, "Вася", "short-game");
            s.Roll("Вася");
            ScenarioAssert.Accepted(s);
            return s;
        }

        var without = Play(null);
        var ordered = Play(new EventRules { DefaultDeck = EventKind.Risky, ApplyOrder = new EquatableArray<EventKind>([EventKind.Bad, EventKind.Good, EventKind.Special]) });

        Assert.Equal(without.Last.Events, ordered.Last.Events);
        Assert.All(Applied(ordered).Modifiers, m => Assert.Null(m.EventKind));
        Assert.Equal(without.Player("Вася"), ordered.Player("Вася"));
    }

    // ---- events.applyOrder: validation and JSON (EV60, S5.4-08) ----

    [Theory]
    [InlineData("Special,Good")]
    [InlineData("Good,Bad")]
    [InlineData("")]
    [InlineData("Special,Good,Bad,Good")]
    [InlineData("Special,Special,Good,Bad")]
    [InlineData("Special,Good,Bad,Risky,Risky")]
    [InlineData("Risky")]
    public void An_order_without_each_of_special_good_and_bad_once_is_refused(string list)
    {
        var on = Order(Parse(list))(TestRuleset.Create().WithEvents());

        Assert.Contains(RulesetValidator.Validate(on), e => e.Path == "events.applyOrder");
    }

    [Theory]
    [InlineData("Special,Good,Bad")]
    [InlineData("Bad,Good,Special")]
    [InlineData("Good,Special,Bad")]
    [InlineData("Risky,Special,Good,Bad")]
    [InlineData("Special,Good,Bad,Risky")]
    [InlineData("Bad,Risky,Good,Special")]
    public void An_order_of_special_good_and_bad_once_and_risky_at_most_once_is_accepted(string list)
    {
        var on = Order(Parse(list))(TestRuleset.Create().WithEvents());

        Assert.DoesNotContain(RulesetValidator.Validate(on), e => e.Path == "events.applyOrder");
    }

    [Fact]
    public void Without_an_order_the_rules_are_valid_and_read_it_as_missing()
    {
        var on = TestRuleset.Create().WithEvents();

        Assert.Null(on.Events!.ApplyOrder);
        Assert.DoesNotContain(RulesetValidator.Validate(on), e => e.Path == "events.applyOrder");

        // A season's rules written before the field still read, strictly, with the field missing (D-50)
        var before = RulesetJson.Parse(WithoutOrder(RulesetJson.DefaultJson()));
        Assert.Null(before.Events!.ApplyOrder);
        Assert.Empty(RulesetValidator.Validate(before));
    }

    // The rules' JSON without the field, as a season created before it has them
    private static string WithoutOrder(string json)
    {
        var without = System.Text.RegularExpressions.Regex.Replace(json, @",\s*""applyOrder"":\s*\[[^\]]*\]", string.Empty);
        Assert.NotEqual(json, without);
        return without;
    }

    [Fact]
    public void The_shipped_rules_write_out_the_spec_order()
    {
        // The shipped default writes the SPEC order out, so a season's rules show it (D-580)
        Assert.Equal(new[] { EventKind.Special, EventKind.Good, EventKind.Bad }, RulesetJson.Default().Events!.ApplyOrder!.Value);
    }

    [Fact]
    public void The_order_is_read_from_events_apply_order_in_the_ruleset_json()
    {
        const string Deck = "\"defaultDeck\": \"risky\"";
        var json = WithoutOrder(RulesetJson.DefaultJson());
        Assert.Contains(Deck, json, StringComparison.Ordinal);

        var ruleset = RulesetJson.Parse(json.Replace(Deck, Deck + ", \"applyOrder\": [\"bad\", \"good\", \"special\"]", StringComparison.Ordinal));

        Assert.Equal(new[] { EventKind.Bad, EventKind.Good, EventKind.Special }, ruleset.Events!.ApplyOrder!.Value);
        Assert.DoesNotContain(RulesetValidator.Validate(ruleset), e => e.Path == "events.applyOrder");
    }

    private static EventKind[] Parse(string list) =>
        [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<EventKind>)];
}
