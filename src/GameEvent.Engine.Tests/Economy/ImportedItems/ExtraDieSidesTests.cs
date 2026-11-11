using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 3 of the import queue (EC42, D-475): <c>modifyDice</c> with <c>stage: count</c> and <c>sides</c> — an extra die of
/// its own kind (a d6 beside the d4 of a normal run), waiting for the next throw or added to the throw just made.
/// </summary>
public class ExtraDieSidesTests
{
    /// <summary>Вася plays Silent Hill (6 hours, normal): two d4 by hours.</summary>
    private static Scenario Playing(params ObjectDefinition[] extra)
    {
        var s = ImportedItemsSeason.New();
        if (extra.Length > 0)
        {
            var pack = EconomyScenario.RepositoryPack();
            s.WithContent(pack with { Objects = [.. pack.Objects, .. extra] });
        }

        return s.RollTitle("Вася", "Silent Hill").Start("Вася");
    }

    [Theory]
    [InlineData("riding-dragon", 6)]
    [InlineData("ratzenstecks", 4)]
    [InlineData("froggoid", 4)]
    [InlineData("pigopithecus", 4)]
    public void An_item_adds_an_extra_die_of_its_kind_to_the_next_throw(string item, int sides)
    {
        var s = Playing();
        s.Give("Вася", item);

        s.Used("Вася", item);
        var waiting = Assert.Single(s.Player("Вася").Wallet.NextDice);
        Assert.Equal((DiceStage.Count, 1, (int?)sides), (waiting.Stage, waiting.Value.Number, waiting.Sides));

        s.NextRandom(1, 2, sides).Complete("Вася");

        var run = s.State.Runs.Values.Single();
        Assert.Equal([4, 4], run.Dice.Select(d => d.Sides));
        var extraDie = Assert.Single(run.DiceMods.ExtraDice);
        Assert.Equal((sides, sides), (extraDie.Sides, extraDie.Value));
        Assert.Equal(3 + sides, s.Player("Вася").Points);
    }

    [Theory]
    [InlineData("rose")]
    [InlineData("cake")]
    public void A_rose_or_a_cake_steps_forward_now_and_adds_a_d4_to_the_next_throw(string item)
    {
        var s = Playing();
        s.Give("Вася", item);

        s.Used("Вася", item);
        Assert.Equal(("c1", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));

        s.NextRandom(1, 1, 4).Complete("Вася");
        Assert.Equal((6, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Extra_dice_of_their_own_kind_follow_those_of_the_runs_kind()
    {
        // A plain extra die (the run's d4) and a riding dragon's d6: the plain one is rolled first, as before the extension
        var s = Playing();
        s.Give("Вася", "extra-die");
        s.Give("Вася", "riding-dragon");
        s.Used("Вася", "riding-dragon").Used("Вася", "extra-die");

        s.NextRandom(1, 1, 3, 5).Complete("Вася");

        var run = s.State.Runs.Values.Single();
        Assert.Equal([(4, 3), (6, 5)], run.DiceMods.ExtraDice.Select(d => (d.Sides, d.Value)));
        Assert.Equal(10, s.Player("Вася").Points);
    }

    [Fact]
    public void A_d8_sides_change_leaves_an_extra_die_of_its_own_kind_alone()
    {
        var d8 = Item("d8-test", DiceWhen.Next, DiceStage.Sides, 8, null, UseWindow.BeforeDice);
        var s = Playing(d8);
        s.Give("Вася", "riding-dragon");
        s.Give("Вася", "d8-test");
        s.Used("Вася", "d8-test").Used("Вася", "riding-dragon");

        s.NextRandom(8, 8, 6).Complete("Вася");

        var run = s.State.Runs.Values.Single();
        Assert.Equal([8, 8], run.Dice.Select(d => d.Sides));
        Assert.Equal(6, Assert.Single(run.DiceMods.ExtraDice).Sides);
        Assert.Equal(22, s.Player("Вася").Points);
    }

    [Fact]
    public void After_the_throw_an_extra_die_of_its_kind_adds_its_points_and_steps()
    {
        var late = Item("late-d6", DiceWhen.Current, DiceStage.Count, 1, 6, UseWindow.AfterDice);
        var s = Playing(late);
        s.NextRandom(1, 1).Complete("Вася");
        Assert.Equal(2, s.Player("Вася").Points);
        s.Give("Вася", "late-d6");

        s.NextRandom(5).Used("Вася", "late-d6");

        var added = Assert.Single(s.LastEvents<Engine.Runs.RunDiceModified>());
        Assert.Equal((6, 5), (Assert.Single(added.Rolled).Sides, Assert.Single(added.Rolled).Value));
        Assert.Equal((7, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Theory]
    [InlineData(DiceStage.Add, 6)]
    [InlineData(DiceStage.Count, 1)]
    [InlineData(DiceStage.Count, 101)]
    public void Sides_belong_to_an_extra_die_of_a_sensible_kind(DiceStage stage, int sides)
    {
        var bad = Item("bad-sides", DiceWhen.Next, stage, 1, sides, UseWindow.BeforeDice);

        Assert.Contains("$.effect.actions[0].sides", ContentValidator.Check(bad).Select(e => e.Path));
    }

    [Fact]
    public void A_waiting_die_of_its_kind_is_written_with_its_sides_and_a_plain_one_as_before()
    {
        var withSides = new NextDiceModified(Guid.Empty, new DiceModifier("riding-dragon", DiceStage.Count, ContentValue.Of(1), Sides: 6));
        var plain = new NextDiceModified(Guid.Empty, new DiceModifier("extra-die", DiceStage.Count, ContentValue.Of(1)));

        Assert.Contains("\"sides\":6", EventCodec.Encode(withSides).Data, StringComparison.Ordinal);
        Assert.DoesNotContain("sides", EventCodec.Encode(plain).Data, StringComparison.Ordinal);
    }

    private static ObjectDefinition Item(string id, DiceWhen when, DiceStage stage, int value, int? sides, UseWindow window) => new()
    {
        Id = id,
        Kind = ObjectKind.Item,
        Name = id,
        Description = id,
        Window = window,
        Effect = new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.Self },
            Actions = [new ModifyDiceAction { When = when, Stage = stage, Value = ContentValue.Of(value), Sides = sides }],
        },
    };
}
