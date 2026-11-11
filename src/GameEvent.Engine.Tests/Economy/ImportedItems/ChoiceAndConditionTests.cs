using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 10 of the import queue (EC49, D-482, D-483): <c>outcomes.choice</c> — a branch the player picks instead of a roll;
/// <c>useCondition</c> with the statistic <c>coins</c> — an item used only while the condition holds; two rolls in one
/// effect without <c>roll.as</c> (each <c>$roll</c> is the latest).
/// </summary>
public class ChoiceAndConditionTests
{
    [Fact]
    public void A_sugar_cube_is_an_extra_d6_or_three_coins_as_the_player_chooses()
    {
        var s = ImportedItemsSeason.New();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Give("Вася", "sugar-cube");
        s.Give("Вася", "sugar-cube");

        s.Used("Вася", "sugar-cube", null, "+3 монетки");
        Assert.Equal(23, s.Player("Вася").Coins);
        Assert.Empty(s.Player("Вася").Wallet.NextDice);

        s.Used("Вася", "sugar-cube", null, "+1d6 к броску");
        var waiting = Assert.Single(s.Player("Вася").Wallet.NextDice);
        Assert.Equal((DiceStage.Count, (int?)6), (waiting.Stage, waiting.Sides));
        Assert.Equal(23, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_sugar_cube_refuses_an_option_it_does_not_offer()
    {
        var s = ImportedItemsSeason.New();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var cube = s.Give("Вася", "sugar-cube");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", cube, null, "+5 монеток"), RejectionCodes.ItemInvalidChoice);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", cube), RejectionCodes.ItemInvalidChoice);
    }

    [Fact]
    public void A_vine_potion_pushes_back_by_a_d4_then_forward_by_a_d6()
    {
        var s = ImportedItemsSeason.New().At("Петя", "c10");
        s.Give("Вася", "vine-potion");

        s.NextRandom(3, 5).Used("Вася", "vine-potion", "Петя");

        var moves = s.LastEvents<Engine.Map.PlayerMoved>().ToList();
        Assert.Equal([-3, 5], moves.Select(m => m.Steps));
        Assert.Equal("c12", s.Player("Петя").CellId);
    }

    [Fact]
    public void A_muffin_is_used_only_with_zero_coins()
    {
        var s = ImportedItemsSeason.New();
        var muffin = s.Give("Вася", "muffin");
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", muffin), RejectionCodes.ItemConditionNotMet);

        s.WithCoins("Вася", -3);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", muffin), RejectionCodes.ItemConditionNotMet);

        s.WithCoins("Вася", 0);
        s.Use("Вася", muffin);
        ScenarioAssert.Accepted(s);
        Assert.Equal(5, s.Player("Вася").Coins);
    }

    [Theory]
    [InlineData("rollAndChoice")]
    [InlineData("optionMissing")]
    [InlineData("optionUnknown")]
    [InlineData("rangeInChoice")]
    public void Choice_outcomes_are_checked(string variant)
    {
        var cube = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "sugar-cube");
        var outcomes = cube.Effect!.Outcomes!;
        var bad = variant switch
        {
            "rollAndChoice" => outcomes with { Roll = ContentValue.TryParse("1d2") },
            "optionMissing" => outcomes with { Cases = [outcomes.Cases[0]] },
            "optionUnknown" => outcomes with { Cases = [outcomes.Cases[0], outcomes.Cases[1] with { Option = "?" }] },
            _ => outcomes with { Cases = [outcomes.Cases[0] with { From = 1, To = 1 }, outcomes.Cases[1]] },
        };

        Assert.NotEmpty(ContentValidator.Check(cube with { Effect = cube.Effect with { Outcomes = bad } }));
    }

    [Theory]
    [InlineData("onEffect")]
    [InlineData("coinsWithoutBound")]
    public void Use_conditions_are_checked(string variant)
    {
        var muffin = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "muffin");
        var bad = variant == "onEffect"
            ? EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "talisman-effect") with { UseCondition = muffin.UseCondition }
            : muffin with { UseCondition = new ConditionSpec { Stat = ContentStat.Coins } };

        Assert.NotEmpty(ContentValidator.Check(bad));
    }
}
