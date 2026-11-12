using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 7 of the import queue (EC46, D-479): effects with a counter. Each firing of the trigger lowers the counter; at zero
/// the actions run (the time bomb: back by d6). <c>transformObject</c> changes counters (<c>addCounter</c>,
/// <c>resetCounters</c>), the filter <c>hasCounter</c> finds such effects, the trigger <c>runEnded</c> fires on a completion
/// and on a drop.
/// </summary>
public class CounterTests
{
    private static readonly string[] s_games = ["Outlast", "Alan Wake", "Limbo", "Inside", "Soma", "Amnesia"];

    /// <summary>Вася and Петя on c20, with games of 6 hours (two d4) enough for a run after run.</summary>
    private static Scenario Season()
    {
        var s = ImportedItemsSeason.New();
        foreach (var title in s_games)
        {
            s.WithGame(title, 6, "Horror");
        }

        return s.At("Вася", "c20").At("Петя", "c20");
    }

    // The next game nobody has completed or dropped yet
    private static string NextGame(Scenario s) =>
        s_games.First(t => !s.State.Runs.Values.Any(r => r.GameId == s.GameId(t)));

    private static Scenario Completes(Scenario s, string player)
    {
        s.RollTitle(player, NextGame(s)).Start(player);
        return s.NextRandom(1, 1).Complete(player);
    }

    private static Scenario Drops(Scenario s, string player)
    {
        s.RollTitle(player, NextGame(s)).Start(player).Advance(TimeSpan.FromMinutes(61));
        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId(player)));
        return s.Last.IsAccepted ? s : throw new InvalidOperationException($"The drop was rejected: {s.Last.Rejection}");
    }

    [Fact]
    public void A_time_bomb_counts_down_on_completions_and_drops_and_goes_off_at_zero()
    {
        var s = Season();
        s.Give("Вася", "time-bomb");
        Assert.Equal(3, s.Held("Вася", "time-bomb").Counter);

        Completes(s, "Вася");
        Assert.Equal(2, s.Held("Вася", "time-bomb").Counter);
        Assert.Empty(s.LastEvents<EffectRolled>());

        Drops(s, "Вася");
        Assert.Equal(1, s.Held("Вася", "time-bomb").Counter);

        var before = s.Player("Вася").CellId;
        s.RollTitle("Вася", NextGame(s)).Start("Вася").NextRandom(1, 1, 5).Complete("Вася");

        // c+2 by the throw, then back by the d6
        var rolled = Assert.Single(s.LastEvents<EffectRolled>());
        Assert.Equal(("time-bomb", 5), (rolled.ObjectId, rolled.Total));
        var back = s.LastEvents<Engine.Map.PlayerMoved>().Last();
        Assert.Equal((-5, Engine.Map.MoveReason.Item), (back.Steps, back.Reason));
        Assert.DoesNotContain("time-bomb", s.Inventory("Вася"));
        Assert.NotEqual(before, s.Player("Вася").CellId);
    }

    [Fact]
    public void A_potion_of_time_lowers_the_counters_of_another_player_and_a_bomb_at_zero_goes_off_at_once()
    {
        var s = Season();
        s.Give("Петя", "time-bomb");
        s.Give("Петя", "talisman-effect");
        s.Give("Вася", "time-potion");
        s.Give("Вася", "time-potion");
        s.Give("Вася", "time-potion");

        s.Used("Вася", "time-potion", "Петя").Used("Вася", "time-potion", "Петя");
        Assert.Equal(1, s.Held("Петя", "time-bomb").Counter);
        Assert.Null(s.Held("Петя", "talisman-effect").Counter);

        s.NextRandom(4).Used("Вася", "time-potion", "Петя");

        Assert.Equal("c16", s.Player("Петя").CellId);
        Assert.Equal(["talisman-effect"], s.Inventory("Петя"));
    }

    [Fact]
    public void A_mossy_scarf_raises_and_an_r_key_resets_the_users_counters()
    {
        var s = Season();
        s.Give("Вася", "time-bomb");
        s.Give("Вася", "mossy-scarf");
        s.Give("Вася", "r-key");
        Completes(s, "Вася");
        Assert.Equal(2, s.Held("Вася", "time-bomb").Counter);

        s.Used("Вася", "mossy-scarf");
        Assert.Equal(3, s.Held("Вася", "time-bomb").Counter);
        Completes(s, "Вася");
        Completes(s, "Вася");
        Assert.Equal(1, s.Held("Вася", "time-bomb").Counter);

        s.Used("Вася", "r-key");
        Assert.Equal(3, s.Held("Вася", "time-bomb").Counter);
    }

    [Fact]
    public void Kefir_destroys_every_own_effect_with_a_counter_without_its_consequences()
    {
        var s = Season();
        s.Give("Вася", "time-bomb");
        s.Give("Вася", "time-bomb");
        s.Give("Вася", "talisman-effect");
        s.Give("Вася", "kefir");

        s.Used("Вася", "kefir");

        Assert.Equal(["talisman-effect"], s.Inventory("Вася"));
        Assert.Empty(s.LastEvents<EffectRolled>());
        Assert.Equal("c20", s.Player("Вася").CellId);
    }

    [Fact]
    public void The_first_finishers_bomb_is_frozen_and_out_of_reach_of_a_potion()
    {
        var s = ImportedItemsSeason.WithFinishers();
        var potion = s.Give("Вася", "time-potion");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", potion, "Петя"), RejectionCodes.ItemInvalidTarget);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", potion, "Коля"), RejectionCodes.ItemInvalidTarget);
    }

    [Fact]
    public void An_object_without_a_counter_is_written_as_before()
    {
        var plain = new ObjectGiven(Guid.Empty, new InventoryObject(Guid.Empty, "talisman-effect", ObjectKind.Effect, 1), ObjectSource.Admin, null);
        var counted = plain with { Item = plain.Item with { Counter = 3 } };

        Assert.DoesNotContain("counter", EventCodec.Encode(plain).Data, StringComparison.Ordinal);
        Assert.Contains("\"counter\":3", EventCodec.Encode(counted).Data, StringComparison.Ordinal);
    }

    // An effect built by an admin from data (AU4-1): at zero it lowers the counters of its owner — its own among them
    private const string ChainBombJson = """
        {
          "id": "chain-bomb",
          "kind": "effect",
          "name": "Цепная бомба",
          "description": "x",
          "rarity": "common",
          "price": null,
          "hostile": false,
          "effect": {
            "trigger": "runEnded",
            "duration": { "untilTriggered": true },
            "counter": { "start": 1 },
            "actions": [ACTIONS]
          }
        }
        """;

    private const string LowerCounters = """{ "type": "transformObject", "mode": "addCounter", "value": -1, "target": "self", "filter": { "hasCounter": true } }""";

    private const string ResetCounters = """{ "type": "transformObject", "mode": "resetCounters", "target": "self", "filter": { "hasCounter": true } }""";

    internal static ObjectDefinition ChainBomb(params string[] actions) =>
        ContentFiles.Pack([("chain-bomb.json", ChainBombJson.Replace("ACTIONS", string.Join(", ", actions), StringComparison.Ordinal))], []).Objects.Single();

    /// <summary>The chain bomb that lowers every counter of its owner when it goes off (the random games play it, AU4-1).</summary>
    internal static ObjectDefinition SelfLoweringBomb() => ChainBomb(LowerCounters);

    private static Scenario WithChainBomb(params string[] actions)
    {
        var s = Season();
        var pack = EconomyScenario.RepositoryPack();
        var bomb = ChainBomb(actions);
        Assert.Empty(ContentValidator.Check(bomb));
        return s.WithContent(pack with { Objects = [.. pack.Objects, bomb] });
    }

    [Fact]
    public void A_countdown_that_lowers_its_own_counters_goes_off_once()
    {
        var s = WithChainBomb(LowerCounters);
        s.Give("Вася", "chain-bomb");

        Completes(s, "Вася");

        // It fires at 1: its actions bring its own counter to zero, so it goes off once more inside — and that is all
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<Engine.Effects.EffectChainCut>());
        Assert.Single(s.LastEvents<ObjectChanged>(), c => c.Item.ObjectId == "chain-bomb");
        Assert.DoesNotContain("chain-bomb", s.Inventory("Вася"));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void A_countdown_at_zero_is_not_lowered_again_and_does_not_go_off_again()
    {
        var s = WithChainBomb(LowerCounters);
        s.Give("Вася", "time-potion");
        s.Give("Петя", "chain-bomb");

        // The potion brings Петя's bomb to zero: it goes off once, and its own action finds its counter already at zero
        s.Used("Вася", "time-potion", "Петя");

        Assert.Single(s.LastEvents<ObjectChanged>(), c => c.Item.ObjectId == "chain-bomb");
        Assert.DoesNotContain("chain-bomb", s.Inventory("Петя"));
    }

    [Fact]
    public void A_countdown_sees_the_counters_its_own_going_off_changed()
    {
        // The chain bomb goes off inside the potion and lowers the time bomb; the potion then lowers it once more: 3 → 2 → 1
        var s = WithChainBomb(LowerCounters);
        s.Give("Вася", "time-potion");
        s.Give("Петя", "chain-bomb");
        s.Give("Петя", "time-bomb");

        s.Used("Вася", "time-potion", "Петя");

        Assert.Equal(1, s.Held("Петя", "time-bomb").Counter);
    }

    [Fact]
    public void A_countdown_at_zero_is_not_wound_up_again()
    {
        // At zero it would reset its counter to 1 and lower it to 0 again: a countdown that went off is left alone
        var s = WithChainBomb(ResetCounters, LowerCounters);
        s.Give("Вася", "chain-bomb");

        Completes(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<Engine.Effects.EffectChainCut>());
        Assert.DoesNotContain("chain-bomb", s.Inventory("Вася"));
    }

    [Fact]
    public void A_countdown_chain_inside_a_command_is_cut()
    {
        // Five bombs, each lowering all the others when it goes off: the first fires on the completion (level 1) and sets
        // off the second (2), the third (3); the fourth would be level 4 — the chain is cut there, the cut written last
        var s = WithChainBomb(LowerCounters);
        for (var i = 0; i < 5; i++)
        {
            s.Give("Вася", "chain-bomb");
        }

        Completes(s, "Вася");

        ScenarioAssert.Accepted(s);
        var cut = Assert.Single(s.LastEvents<Engine.Effects.EffectChainCut>());
        Assert.Equal((Engine.Effects.EffectChainLimit.Depth, Limits.MaxEffectDepth + 1), (cut.Limit, cut.Depth));
        Assert.Same(cut, s.Last.Events[^1]);
        Assert.True(s.Last.Events.Count <= Limits.MaxEventsPerCommand + 1);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void A_countdown_chain_set_off_by_an_item_is_cut_by_the_chain_depth()
    {
        // The potion is the command (level 0): the bombs it sets off go off on levels 1, 2, 3; the next one is cut
        var s = WithChainBomb(LowerCounters);
        for (var i = 0; i < 5; i++)
        {
            s.Give("Петя", "chain-bomb");
        }

        s.Give("Вася", "time-potion");

        s.Used("Вася", "time-potion", "Петя");

        var cut = Assert.Single(s.LastEvents<Engine.Effects.EffectChainCut>());
        Assert.Equal((Engine.Effects.EffectChainLimit.Depth, Limits.MaxEffectDepth + 1), (cut.Limit, cut.Depth));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Theory]
    [InlineData("onItem")]
    [InlineData("zero")]
    [InlineData("addWithoutValue")]
    [InlineData("resetWithValue")]
    public void Counters_are_checked(string variant)
    {
        var bomb = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "time-bomb");
        var potion = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "time-potion");
        var bad = variant switch
        {
            "onItem" => potion with { Effect = potion.Effect! with { Counter = new CounterSpec { Start = 3 } } },
            "zero" => bomb with { Effect = bomb.Effect! with { Counter = new CounterSpec { Start = 0 } } },
            "addWithoutValue" => potion with { Effect = potion.Effect! with { Actions = [new TransformObjectAction { Mode = TransformMode.AddCounter }] } },
            _ => potion with { Effect = potion.Effect! with { Actions = [new TransformObjectAction { Mode = TransformMode.ResetCounters, Value = ContentValue.Of(1) }] } },
        };

        Assert.NotEmpty(ContentValidator.Check(bad));
    }
}
