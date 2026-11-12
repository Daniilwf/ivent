using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 9 of the import queue (EC48, D-481): the rare stages of the throw's pipeline, one by one — <c>singleDie</c>,
/// <c>setOne</c>, <c>replaceValue</c>, <c>fixAverage</c>, <c>bonusPerEven</c>, <c>percent</c>, and <c>reroll</c> with the
/// filter <c>belowAverage</c>. Faces are kept apart from the dice, so a correction of the hours keeps what items did (D-14).
/// Вася plays Silent Hill (6 hours, normal: two d4) unless said otherwise.
/// </summary>
public class RareDiceStagesTests
{
    private static Scenario Playing(string game = "Silent Hill")
    {
        var s = ImportedItemsSeason.New();
        s.WithGame("Gothic", 12, "Horror");
        return s.RollTitle("Вася", game).Start("Вася");
    }

    private static RunState Run(Scenario s) => s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Вася"));

    [Fact]
    public void A_d8_die_throws_one_d8_instead_of_the_runs_dice_and_keeps_it_through_a_correction()
    {
        var s = Playing("Gothic");
        s.Give("Вася", "d8-die");
        s.Used("Вася", "d8-die");

        // Gothic, 12 hours: four d4 — one d8 instead
        s.NextRandom(7).Complete("Вася");
        var run = Run(s);
        Assert.Equal([(8, 7)], run.Dice.Select(d => (d.Sides, d.Value)));
        Assert.Equal(7, s.Player("Вася").Points);

        // The hours change: still one d8 (the count stage keeps the item's die)
        s.Act(new CorrectRunHours(run.RunId, 24, "Уточнил время"));
        ScenarioAssert.Accepted(s);
        Assert.Single(Run(s).Dice);
        Assert.Equal(7, s.Player("Вася").Points);
    }

    [Fact]
    public void A_d8_die_keeps_its_one_d8_through_a_change_of_difficulty()
    {
        var s = Playing("Gothic");
        s.Give("Вася", "d8-die");
        s.Used("Вася", "d8-die");
        s.NextRandom(5).Complete("Вася");
        Assert.Equal(5, s.Player("Вася").Points);

        // Hard plays d6: the item's d8 is not recalculated into the difficulty's die (D-481, D-408)
        s.Act(new ChangeRunDifficulty(Run(s).RunId, Difficulty.Hard, "Уточнил сложность"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([(8, 5)], Run(s).Dice.Select(d => (d.Sides, d.Value)));
        Assert.Equal(5, s.Player("Вася").Points);
    }

    [Fact]
    public void A_reroll_after_a_d8_die_throws_one_d8_again_and_the_challenge_dice_of_the_difficulty()
    {
        var s = ImportedItemsSeason.New();
        s.WithRuleset(r => r with { Features = r.Features with { Challenges = true } }).WithGame("Gothic", 12, "Horror");
        s.RollTitle("Вася", "Gothic").Start("Вася");
        s.Give("Вася", "d8-die");
        s.Used("Вася", "d8-die");
        s.Complete("Вася", Difficulty.Hard, challengeDone: true);
        var challenge = Run(s).ChallengeDice.Count;
        Assert.True(challenge > 0);
        s.Give("Вася", "reroll-dice");

        s.NextRandom(8).Used("Вася", "reroll-dice");

        var run = Run(s);
        Assert.Equal([(8, 8)], run.Dice.Select(d => (d.Sides, d.Value)));
        Assert.Equal(challenge, run.ChallengeDice.Count);
        Assert.All(run.ChallengeDice, d => Assert.Equal(6, d.Sides));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    public void Romans_die_sets_one_thrown_die_to_one_or_three(string answer, int face)
    {
        var s = Playing();
        s.Give("Вася", "romans-die");
        s.Used("Вася", "romans-die", null, answer);

        s.NextRandom(4, 2).Complete("Вася");

        Assert.Equal(face + 2, s.Player("Вася").Points);
        Assert.Equal([4, 2], Run(s).Dice.Select(d => d.Value));
    }

    [Theory]
    [InlineData("4", 6)]
    [InlineData("20", 6)]
    [InlineData("1", 3)]
    public void A_cheaters_die_sets_one_die_to_the_value_chosen_but_not_above_its_sides(string answer, int points)
    {
        var s = Playing();
        s.Give("Вася", "cheaters-die");
        s.Used("Вася", "cheaters-die", null, answer);

        s.NextRandom(1, 2).Complete("Вася");

        Assert.Equal(points, s.Player("Вася").Points);
    }

    [Fact]
    public void A_fifth_die_counts_every_five_as_seven()
    {
        var s = Playing("Gothic");
        s.Give("Вася", "fifth-die");
        s.Used("Вася", "fifth-die");

        // On d6 (hard) two fives count 14
        s.NextRandom(5, 5, 1, 2).Complete("Вася", Difficulty.Hard);

        Assert.Equal(7 + 7 + 1 + 2, s.Player("Вася").Points);
    }

    [Fact]
    public void A_middle_die_makes_every_thrown_die_its_average()
    {
        var s = Playing("Gothic");
        s.Give("Вася", "middle-die");
        s.Used("Вася", "middle-die");

        s.NextRandom(1, 6, 6, 1).Complete("Вася", Difficulty.Hard);

        // d6 = 3 each
        Assert.Equal(12, s.Player("Вася").Points);
    }

    [Fact]
    public void A_happy_die_adds_a_step_for_every_even_die()
    {
        var s = Playing("Gothic");
        s.Give("Вася", "happy-die");
        s.Used("Вася", "happy-die");

        s.NextRandom(2, 4, 1, 3).Complete("Вася");

        Assert.Equal(10 + 2, s.Player("Вася").Points);
        Assert.Equal("c12", s.Player("Вася").CellId);
    }

    [Theory]
    [InlineData(3, 1, 6)]
    [InlineData(1, 2, 4)]
    public void Mythic_boots_add_half_of_the_throw_rounded_toward_zero(int first, int second, int points)
    {
        var s = Playing();
        s.Give("Вася", "mythic-boots");
        s.Used("Вася", "mythic-boots");

        s.NextRandom(first, second).Complete("Вася");

        Assert.Equal(points, s.Player("Вася").Points);
    }

    [Fact]
    public void A_second_chance_die_rerolls_only_the_dice_below_average_after_the_throw()
    {
        var s = Playing("Gothic");
        s.NextRandom(1, 2, 3, 4).Complete("Вася");
        Assert.Equal(10, s.Player("Вася").Points);
        s.Give("Вася", "second-chance-die");

        // d4: only the 1 is below average (2); it becomes a 4
        s.NextRandom(4).Used("Вася", "second-chance-die");

        Assert.Equal([4, 2, 3, 4], Run(s).Dice.Select(d => d.Value));
        Assert.Equal(13, s.Player("Вася").Points);
        Assert.Equal("c13", s.Player("Вася").CellId);
    }

    [Fact]
    public void Faces_changed_by_items_survive_a_correction_of_the_hours()
    {
        var s = Playing();
        s.Give("Вася", "middle-die");
        s.Used("Вася", "middle-die");
        s.NextRandom(1, 4).Complete("Вася");
        Assert.Equal(4, s.Player("Вася").Points);

        // 12 hours: four d4, the two new ones are averages too
        s.NextRandom(1, 1).Act(new CorrectRunHours(Run(s).RunId, 12, "Уточнил время"));

        Assert.Equal(8, s.Player("Вася").Points);
    }

    [Theory]
    [InlineData(DiceStage.SingleDie, 1, null, null)]
    [InlineData(DiceStage.ReplaceValue, 7, null, null)]
    [InlineData(DiceStage.Add, 1, 5, null)]
    [InlineData(DiceStage.Add, 1, null, DieFilter.BelowAverage)]
    [InlineData(DiceStage.Percent, 0, null, null)]
    public void Rare_stages_are_checked(DiceStage stage, int value, int? from, DieFilter? filter)
    {
        var item = new ObjectDefinition
        {
            Id = "bad-stage",
            Kind = ObjectKind.Item,
            Name = "x",
            Description = "x",
            Window = UseWindow.BeforeDice,
            Effect = new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.Self },
                Actions = [new ModifyDiceAction { When = DiceWhen.Next, Stage = stage, Value = ContentValue.Of(value), From = from, Filter = filter }],
            },
        };

        Assert.NotEmpty(ContentValidator.Check(item));
    }

    [Fact]
    public void Mods_without_the_rare_stages_are_written_as_before()
    {
        var mods = RunDiceMods.None with { Added = 2 };

        Assert.Equal("""{"added":2,"multiplier":1}""", System.Text.Json.JsonSerializer.Serialize(mods, EngineJson.Options));
    }
}
