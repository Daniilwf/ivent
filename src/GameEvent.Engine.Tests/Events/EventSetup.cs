using GameEvent.Engine.Content;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Shared setup of the task 5.3 tests (sources of events, manual events, finishers, the Бабл). Events and items are written
/// as content JSON, as the admin uploads them, so a test does not depend on the C# shape of new fields (<c>intercept</c> as a
/// set, D-484). Each test publishes only the objects it needs: a pack the engine does not accept yet fails the setup of the
/// tests that need it and no other. Pinned test ruleset: 3 hours = one die, normal d4, extreme d6 with a good event, drop
/// 2d4 on points and position with a bad event.
/// </summary>
internal static class EventSetup
{
    public const string Proof = "Видел на стриме";

    /// <summary>Objects of these tests beyond the CONTENT.md examples, as content JSON.</summary>
    private static readonly Dictionary<string, string> s_json = new(StringComparer.Ordinal)
    {
        // ---- Events ----
        ["coins-minus-3"] = Event("coins-minus-3", "Штраф", """[{ "type": "changeResource", "resource": "coins", "amount": -3 }]"""),
        ["coins-plus-2"] = Event("coins-plus-2", "Подарок", """[{ "type": "changeResource", "resource": "coins", "amount": 2 }]"""),
        ["coin-gift"] = Event("coin-gift", "Монетка", """[{ "type": "changeResource", "resource": "coins", "amount": 1 }]"""),
        ["minus-die"] = Event("minus-die", "Тяжёлая рука", """[{ "type": "modifyDice", "when": "next", "stage": "add", "value": "-1d6" }]"""),
        ["hop-and-coins"] = Event(
            "hop-and-coins",
            "Прыжок с кошельком",
            """[{ "type": "move", "steps": 2 }, { "type": "changeResource", "resource": "coins", "amount": 3 }]"""),
        ["bounty"] = Event(
            "bounty",
            "Клад",
            """
            [
              { "type": "giveObject", "objectId": "shield" },
              { "type": "modifyNextRoll", "choiceCount": 3 },
              { "type": "changeResource", "resource": "coins", "amount": 5 },
              { "type": "changeResource", "resource": "points", "amount": 2 }
            ]
            """),
        ["toll"] = Event(
            "toll",
            "Пошлина",
            """[{ "type": "changeResource", "resource": "coins", "amount": -3, "target": "self" }, { "type": "changeResource", "resource": "coins", "amount": -2 }]""",
            """{ "selector": "leader" }"""),
        ["pickpocket"] = Event(
            "pickpocket",
            "Карманник",
            """[{ "type": "changeResource", "resource": "coins", "amount": -2 }]""",
            """{ "selector": "randomActive" }"""),

        // ---- Items that draw ----
        ["sack-special"] = Item("sack-special", "Мешок ивентов", hostile: false, "self", "special"),
        ["sack-bad"] = Item("sack-bad", "Чёрный мешок", hostile: false, "self", "bad"),
        ["sack-good"] = Item("sack-good", "Белый мешок", hostile: false, "self", "good"),
        ["sack-risky"] = Item("sack-risky", "Пёстрый мешок", hostile: false, "self", "risky"),
        ["jinx"] = Item("jinx", "Сглаз", hostile: true, "chosen", "bad"),

        // ---- The Бабл as imported (content/imports/old-items/items.json, D-484, panel D-484) ----
        ["babl"] = """
            {
              "id": "babl", "kind": "item", "name": "Бабл",
              "description": "Дает Эффект - Анулирует следующий Плохой Ивент, уничтожается после использования эффекта",
              "rarity": "epic", "price": 30, "window": "anytime", "hostile": false, "stackable": true, "tags": [],
              "effect": { "target": { "selector": "self" }, "actions": [{ "type": "giveObject", "objectId": "bubble-effect" }] }
            }
            """,
        ["bubble-effect"] = """
            {
              "id": "bubble-effect", "kind": "effect", "name": "Бабл",
              "description": "Гасит следующий плохой ивент или враждебный эффект.",
              "rarity": "epic", "price": null, "hostile": false, "tags": [],
              "effect": { "trigger": "hostileIncoming", "intercept": ["badEvent", "hostile"], "duration": { "uses": 1 }, "actions": [] }
            }
            """,
    };

    private static string Event(string id, string name, string actions, string target = """{ "selector": "self" }""") => $$"""
        {
          "id": "{{id}}", "kind": "event", "name": "{{name}}", "description": "{{name}}.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": {{target}}, "actions": {{actions}} }
        }
        """;

    private static string Item(string id, string name, bool hostile, string selector, string deck) => $$"""
        {
          "id": "{{id}}", "kind": "item", "name": "{{name}}", "description": "{{name}}: вытяни ивент.",
          "rarity": "common", "price": 10, "window": "anytime", "hostile": {{(hostile ? "true" : "false")}}, "stackable": true,
          "effect": { "target": { "selector": "{{selector}}" }, "actions": [{ "type": "drawEvent", "deck": "{{deck}}" }] }
        }
        """;

    /// <summary>An object by id: one of these tests, else a CONTENT.md example.</summary>
    public static ObjectDefinition Object(string id) =>
        s_json.TryGetValue(id, out var json) ? ContentJson.Parse<ObjectDefinition>(json) : EconomyScenario.Examples()[id];

    /// <summary>A pack of the objects <paramref name="ids"/> and the decks.</summary>
    public static ContentPack Pack(string[] ids, params WheelDefinition[] decks) => new()
    {
        Objects = [.. ids.Distinct(StringComparer.Ordinal).Select(Object).OrderBy(o => o.Id, StringComparer.Ordinal)],
        Wheels = [.. decks.OrderBy(d => d.Id, StringComparer.Ordinal)],
    };

    public static WheelDefinition Deck(EventKind kind, params string[] events) => EventScenario.Deck(kind, null, events);

    /// <summary>start → c1 … c12 → finish; <paramref name="eventCells"/> are event cells (cell id, deck).</summary>
    public static MapGraph Line(params (string Cell, string Deck)[] eventCells)
    {
        var map = MapBuilder.New();
        foreach (var (cell, deck) in eventCells)
        {
            map = map.EventCell(cell, deck);
        }

        return map.Path(["start", .. Enumerable.Range(1, 12).Select(i => $"c{i}"), "finish"]).Build();
    }

    /// <summary>
    /// A season with events on (and items), on <paramref name="map"/> (default <see cref="Line"/>), one Horror category of
    /// 3-hour games and one 6-hour game, the players, the pack published. <paramref name="rules"/> change the ruleset first.
    /// </summary>
    public static Scenario Season(
        ContentPack pack,
        MapGraph? map = null,
        Func<Ruleset, Ruleset>? rules = null,
        int seed = 42,
        string[]? players = null)
    {
        var ruleset = TestRuleset.Create().WithEvents();
        var s = Scenario.New(rules is null ? ruleset : rules(ruleset), seed).WithMap(map ?? Line())
            .WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror")
            .WithGame("Alan Wake", 6, "Horror")
            .WithPlayers(players ?? ["Вася", "Петя"]);
        return s.WithContent(pack);
    }

    /// <summary>The admin puts the player on <paramref name="cell"/>.</summary>
    public static Scenario At(this Scenario s, string player, string cell)
    {
        s.Act(new AdjustPlayer(s.PlayerId(player), "Позиция для теста", CellId: cell));
        return s.Last.IsAccepted ? s : throw new InvalidOperationException($"Moving {player} was rejected: {s.Last.Rejection}");
    }

    /// <summary>The player rolls <paramref name="title"/>, starts it and plays past the hour a drop needs; returns the run.</summary>
    public static Guid PlayingFor(this Scenario s, string player, string title = "Silent Hill")
    {
        s.RollTitle(player, title).Start(player);
        s.Advance(TimeSpan.FromMinutes(61));
        return s.Player(player).ActiveRunId!.Value;
    }

    /// <summary>The player drops; <paramref name="random"/> (penalty dice, then draw tickets) is scripted first.</summary>
    public static Scenario Dropped(this Scenario s, string player, params int[] random)
    {
        s.NextRandom(random).Act(new Engine.Runs.DropRun(s.PlayerId(player)));
        ScenarioAssert.Accepted(s);
        return s;
    }

    /// <summary>The player gives himself a Бабл and uses it: the effect «Бабл» waits; returns its instance.</summary>
    public static Guid Bubbled(this Scenario s, string player)
    {
        s.Give(player, "babl");
        s.Used(player, "babl");
        return s.Held(player, "bubble-effect").InstanceId;
    }

    public static Scenario Approve(this Scenario s, Guid runId)
    {
        s.Act(new ApproveProof(runId, null, Proof));
        ScenarioAssert.Accepted(s);
        return s;
    }

    /// <summary>Index of the first event of the last command matching <paramref name="match"/>; −1 when there is none.</summary>
    public static int IndexOf(this Scenario s, Func<GameEvent.Engine.Kernel.IGameEvent, bool> match)
    {
        for (var i = 0; i < s.Last.Events.Count; i++)
        {
            if (match(s.Last.Events[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
