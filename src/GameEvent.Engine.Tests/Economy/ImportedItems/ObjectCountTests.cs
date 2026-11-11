using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Economy.ImportedItems.ImportedItemsSeason;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 4 of the import queue (EC43, D-476): <c>takeObject</c> takes several objects — <c>count</c> (a number or a
/// reference such as <c>$roll</c>) or <c>all: true</c>; with <c>pick: chosen</c> a fixed count asks one answer per object,
/// a count known only at the use takes the answers as a list of preference and the rest at random.
/// </summary>
public class ObjectCountTests
{
    [Fact]
    public void A_double_gremlin_destroys_the_two_chosen_items_for_two_coins()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        s.Give("Петя", "pizza");
        var beer = s.Give("Петя", "light-beer");
        s.Give("Вася", "double-gremlin");

        s.Used("Вася", "double-gremlin", "Петя", Answer(candy), Answer(beer));

        Assert.Equal(["pizza"], s.Inventory("Петя"));
        Assert.Equal(2, s.LastEvents<ObjectRemoved>().Count(r => r.Reason == ObjectRemoval.Destroyed));
        Assert.Equal(18, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_double_gremlin_asks_one_answer_per_item_and_each_once()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        s.Give("Петя", "pizza");
        var gremlin = s.Give("Вася", "double-gremlin");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", gremlin, "Петя", Answer(candy)), RejectionCodes.ItemInvalidChoice);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", gremlin, "Петя", Answer(candy), Answer(candy)), RejectionCodes.ItemInvalidChoice);
    }

    [Fact]
    public void A_double_gremlin_on_a_single_item_takes_one_answer()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        s.Give("Петя", "gremlin");
        var gremlin = s.Give("Вася", "double-gremlin");

        s.Use("Вася", gremlin, "Петя", Answer(candy));

        ScenarioAssert.Accepted(s);
        Assert.Equal(["gremlin"], s.Inventory("Петя"));
    }

    [Fact]
    public void A_better_rat_steals_two_chosen_items_and_loses_what_does_not_fit()
    {
        var s = ImportedItemsSeason.New();
        foreach (var item in new[] { "candy", "pizza", "cupcake", "chocolate" })
        {
            s.Give("Вася", item);
        }

        var a = s.Give("Петя", "sausage");
        var b = s.Give("Петя", "tomato");
        s.Give("Вася", "better-rat");

        // The rat leaves Вася's hands first: 4 items, room for one more (limit 5); the second is lost (D-406)
        s.Used("Вася", "better-rat", "Петя", Answer(a), Answer(b));

        Assert.Contains("sausage", s.Inventory("Вася"));
        Assert.DoesNotContain("tomato", s.Inventory("Вася"));
        Assert.Empty(s.Inventory("Петя"));
        Assert.Single(s.LastEvents<ObjectLost>());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    public void A_plasma_gun_destroys_as_many_items_as_its_d4_shows_the_listed_first(int face, int destroyed)
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        var pizza = s.Give("Петя", "pizza");
        var beer = s.Give("Петя", "light-beer");
        s.Give("Вася", "plasma-gun");

        s.NextRandom(face).Used("Вася", "plasma-gun", "Петя", Answer(pizza), Answer(candy), Answer(beer));

        var gone = s.LastEvents<ObjectRemoved>().Where(r => r.Reason == ObjectRemoval.Destroyed).Select(r => r.InstanceId).ToList();
        Assert.Equal(destroyed, gone.Count);
        Assert.Equal(new[] { pizza, candy, beer }.Take(destroyed), gone);
    }

    [Fact]
    public void A_plasma_gun_destroys_the_rest_at_random_when_fewer_were_listed()
    {
        var s = ImportedItemsSeason.New();
        var candy = s.Give("Петя", "candy");
        s.Give("Петя", "pizza");
        s.Give("Петя", "light-beer");
        s.Give("Вася", "plasma-gun");

        s.NextRandom(2).Used("Вася", "plasma-gun", "Петя", Answer(candy));

        var gone = s.LastEvents<ObjectRemoved>().Where(r => r.Reason == ObjectRemoval.Destroyed).ToList();
        Assert.Equal(2, gone.Count);
        Assert.Equal(candy, gone[0].InstanceId);
        Assert.Single(s.Inventory("Петя"));
    }

    [Fact]
    public void A_plasma_gun_refuses_an_answer_that_is_not_an_item_of_the_target()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Петя", "candy");
        var mine = s.Give("Вася", "pizza");
        var gun = s.Give("Вася", "plasma-gun");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.NextRandom(1).Use("Вася", gun, "Петя", Answer(mine)), RejectionCodes.ItemInvalidChoice);
    }

    [Fact]
    public void A_rare_gremlin_destroys_every_item_but_gremlins_for_ten_coins()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Петя", "candy");
        s.Give("Петя", "pizza");
        s.Give("Петя", "fed-gremlin");
        s.Give("Петя", "curse-effect");
        s.Give("Вася", "rare-gremlin");

        s.Used("Вася", "rare-gremlin", "Петя");

        Assert.Equal(["fed-gremlin", "curse-effect"], s.Inventory("Петя"));
        Assert.Equal(10, s.Player("Вася").Coins);
    }

    [Fact]
    public void An_epic_gremlin_destroys_every_object_but_gremlins_for_fifteen_coins()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Петя", "candy");
        s.Give("Петя", "fed-gremlin");
        s.Give("Петя", "curse-effect");
        s.Give("Петя", "forced-genre");
        s.Give("Вася", "epic-gremlin");

        s.Used("Вася", "epic-gremlin", "Петя");

        Assert.Equal(["fed-gremlin"], s.Inventory("Петя"));
        Assert.Equal(5, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_count_of_all_on_nothing_to_take_is_still_a_use()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Вася", "rare-gremlin");

        s.Used("Вася", "rare-gremlin", "Петя");

        Assert.DoesNotContain(s.LastEvents<ObjectRemoved>(), r => r.Reason == ObjectRemoval.Destroyed);
        Assert.Equal(10, s.Player("Вася").Coins);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("allAndCount")]
    [InlineData("allChosen")]
    [InlineData("dynamicThenChoice")]
    [InlineData("rollWithoutRoll")]
    public void Counts_are_checked(string variant)
    {
        TakeObjectAction take = new() { Mode = TakeMode.Destroy, Pick = Pick.Chosen };
        ActionSpec[] actions = variant switch
        {
            "zero" => [take with { Count = ContentValue.Of(0) }],
            "allAndCount" => [take with { All = true, Count = ContentValue.Of(2), Pick = Pick.Random }],
            "allChosen" => [take with { All = true }],
            "dynamicThenChoice" =>
            [
                new RollAction { Dice = ContentValue.TryParse("1d4")! },
                take with { Count = ContentValue.TryParse("$roll")! },
                new RequestChoiceAction { Prompt = "?", Options = new ChoiceOptionsSpec { List = ["a", "b"] } },
            ],
            _ => [take with { Count = ContentValue.TryParse("$roll")! }],
        };
        var item = new ObjectDefinition
        {
            Id = "bad-count",
            Kind = ObjectKind.Item,
            Name = "x",
            Description = "x",
            Window = UseWindow.Anytime,
            Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Chosen }, Actions = [.. actions] },
        };

        Assert.NotEmpty(ContentValidator.Check(item));
    }
}
