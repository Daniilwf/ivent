using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC21 (D-440): the plan of an inventory — what the screen offers — is the engine's own rules: «Использовать» is open
/// exactly when the window and the guard let it, the targets offered are exactly the ones a use accepts (the first
/// finisher never, a later finisher only for points and coins), and the answers asked are the options the engine checks.
/// </summary>
public class ItemPlansTests
{
    // Петя finished first, Вася second (both on a 2-cell map), Коля plays on
    private static Scenario Finishers()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithGame("Gothic", 30, "RPG")
            .WithMapLength(2).WithPlayers("Вася", "Петя", "Коля");
        s.WithContent(EconomyScenario.RepositoryPack());
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        s.RollTitle("Вася", "Dead Space").Start("Вася").NextRandom(1, 1, 1).Complete("Вася");
        return s;
    }

    private static IPoolView Pool(Scenario s) => s.Context().Pool;

    private static ItemPlan PlanOf(Scenario s, string player, string objectId) =>
        ItemPlans.Of(s.State, Pool(s), s.Clock.UtcNow, s.PlayerId(player)).Single(p => p.Item.ObjectId == objectId);

    [Theory]
    [InlineData("shove")]
    [InlineData("bird-thief")]
    [InlineData("point-thief")]
    [InlineData("curse")]
    [InlineData("magnet")]
    public void The_targets_offered_are_exactly_the_ones_a_use_accepts(string objectId)
    {
        var s = Finishers().WithPoints("Вася", 1).WithPoints("Коля", 0);
        s.Give("Коля", objectId);
        s.Give("Вася", "orange");
        var plan = PlanOf(s, "Коля", objectId);

        Assert.True(plan.ChoosesTarget);
        foreach (var target in s.State.Players.Keys)
        {
            var decision = SeasonEngine.Execute(s.State, new UseItem(s.PlayerId("Коля"), plan.Item.InstanceId, target), s.Context());
            var refusedAsTarget = decision.Rejection?.Code is RejectionCodes.ItemInvalidTarget;
            Assert.True(
                plan.Targets.Contains(target) != refusedAsTarget,
                $"{objectId} on {s.State.Players[target].Name}: offered {plan.Targets.Contains(target)}, refused as a target {refusedAsTarget}.");
        }
    }

    [Fact]
    public void The_first_finisher_is_never_offered_and_a_later_finisher_only_for_points_and_coins()
    {
        var s = Finishers();
        s.Give("Коля", "shove");
        s.Give("Коля", "bird-thief");

        Assert.DoesNotContain(s.PlayerId("Петя"), PlanOf(s, "Коля", "bird-thief").Targets);
        Assert.Contains(s.PlayerId("Вася"), PlanOf(s, "Коля", "bird-thief").Targets);
        Assert.Equal([], PlanOf(s, "Коля", "shove").Targets);
        Assert.Equal(RejectionCodes.ItemNoTarget, PlanOf(s, "Коля", "shove").Refusal);
    }

    [Fact]
    public void An_item_in_its_window_is_usable_and_out_of_it_says_why()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
        s.WithContent(EconomyScenario.RepositoryPack());
        s.Give("Вася", "orange");
        var beforeDice = s.State.Catalog.Objects.Values.Select(e => e.Definition)
            .First(d => d.Kind == ObjectKind.Item && d.Window == UseWindow.BeforeDice);
        s.Give("Вася", beforeDice.Id);

        Assert.True(PlanOf(s, "Вася", "orange").Usable);
        Assert.Equal(RejectionCodes.ItemWrongWindow, PlanOf(s, "Вася", beforeDice.Id).Refusal);

        s.RollTitle("Вася", "Silent Hill").Start("Вася");

        Assert.True(PlanOf(s, "Вася", beforeDice.Id).Usable);
    }

    [Fact]
    public void An_effect_is_shown_but_never_used()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
        s.WithContent(EconomyScenario.RepositoryPack());
        s.Give("Вася", "curse-effect");

        var plan = PlanOf(s, "Вася", "curse-effect");

        Assert.Equal(RejectionCodes.ItemNotAnItem, plan.Refusal);
        Assert.False(plan.Usable);
    }

    [Fact]
    public void A_choice_asks_for_the_options_the_engine_accepts()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithCategory("RPG").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя", "Коля");
        s.WithContent(EconomyScenario.RepositoryPack()).WithPoints("Петя", 5);
        s.Give("Коля", "dirty-trick");

        var plan = PlanOf(s, "Коля", "dirty-trick");

        var step = Assert.Single(plan.Steps);
        Assert.Equal(ItemStepKind.Choice, step.Kind);
        Assert.Equal(["Horror", "RPG"], (IReadOnlyList<string>)step.Options);
        Assert.Equal("Какой жанр подкинуть?", step.Prompt);
        Assert.Equal([s.PlayerId("Петя")], (IReadOnlyList<Guid>)plan.Targets);
        var used = SeasonEngine.Execute(s.State, new UseItem(s.PlayerId("Коля"), plan.Item.InstanceId, plan.Targets[0], ["RPG"]), s.Context());
        Assert.True(used.IsAccepted, used.Rejection?.Detail);
    }

    [Fact]
    public void After_the_deadline_nothing_is_usable()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
        s.WithContent(EconomyScenario.RepositoryPack());
        s.Give("Вася", "orange");
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(1)));
        s.Advance(TimeSpan.FromHours(2));

        Assert.Equal(RejectionCodes.SeasonDeadlinePassed, PlanOf(s, "Вася", "orange").Refusal);
    }

    [Fact]
    public void With_items_off_nothing_is_usable_and_a_stranger_has_no_plan()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
        s.WithContent(EconomyScenario.RepositoryPack());
        s.Give("Вася", "orange");
        s.WithRuleset(r => r with { Features = r.Features with { Items = false, Shop = false } });

        Assert.Equal(RejectionCodes.FeatureDisabled, PlanOf(s, "Вася", "orange").Refusal);
        Assert.Empty(ItemPlans.Of(s.State, Pool(s), s.Clock.UtcNow, Guid.NewGuid()));
    }
}
