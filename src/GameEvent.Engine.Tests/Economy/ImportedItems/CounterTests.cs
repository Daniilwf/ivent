using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
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
