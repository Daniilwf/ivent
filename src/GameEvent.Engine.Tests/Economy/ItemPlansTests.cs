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
    private static Scenario Finishers(params ObjectDefinition[] extra)
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithGame("Gothic", 30, "RPG")
            .WithMapLength(2).WithPlayers("Вася", "Петя", "Коля");
        var pack = EconomyScenario.RepositoryPack();
        s.WithContent(pack with { Objects = [.. pack.Objects, .. extra] });
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

    // ---- Targets the selector picks itself (not «chosen»): nobody to act on is known without a roll ----

    private static ObjectDefinition Fixture(string id, TargetSelector selector, ActionSpec action, bool excludeSelf = true) =>
        new()
        {
            Id = id,
            Kind = ObjectKind.Item,
            Name = "Проверка",
            Description = "Предмет для проверки плана.",
            Rarity = Rarity.Common,
            Window = UseWindow.Anytime,
            Hostile = false,
            Stackable = true,
            Effect = new EffectSpec { Target = new TargetSpec { Selector = selector, ExcludeSelf = excludeSelf }, Actions = [action] },
        };

    private static readonly MoveAction s_back = new() { Steps = ContentValue.Of(-1) };

    private static readonly ChangeResourceAction s_levy = new() { Resource = "coins", Amount = ContentValue.Of(-1) };

    private static ObjectDefinition[] Selectors() =>
    [
        Fixture("push-leader", TargetSelector.Leader, s_back),
        Fixture("push-random", TargetSelector.RandomActive, s_back),
        Fixture("push-all", TargetSelector.All, s_back),
        Fixture("push-higher", TargetSelector.HigherPoints, s_back),
        Fixture("levy-leader", TargetSelector.Leader, s_levy),
        Fixture("levy-random", TargetSelector.RandomActive, s_levy),
        Fixture("levy-all", TargetSelector.All, s_levy),
    ];

    [Fact]
    public void A_later_finisher_is_not_offered_an_item_that_only_moves_himself()
    {
        var s = Finishers();
        s.Give("Вася", "banana");

        var plan = PlanOf(s, "Вася", "banana");
        var use = SeasonEngine.Execute(s.State, new UseItem(s.PlayerId("Вася"), plan.Item.InstanceId), s.Context());

        Assert.Equal(RejectionCodes.ItemNoTarget, use.Rejection?.Code);
        Assert.Equal(RejectionCodes.ItemNoTarget, plan.Refusal);
        Assert.False(plan.Usable);
    }

    [Theory]
    [InlineData("Вася", "banana")]
    [InlineData("Вася", "orange")]
    [InlineData("Коля", "banana")]
    [InlineData("Вася", "push-leader")]
    [InlineData("Коля", "push-leader")]
    [InlineData("Коля", "push-random")]
    [InlineData("Коля", "push-all")]
    [InlineData("Коля", "push-higher")]
    [InlineData("Коля", "levy-leader")]
    [InlineData("Коля", "levy-random")]
    [InlineData("Вася", "levy-random")]
    [InlineData("Коля", "levy-all")]
    public void Nobody_to_act_on_is_said_exactly_when_a_use_says_it(string player, string objectId)
    {
        var s = Finishers(Selectors()).WithPoints("Коля", 3).WithPoints("Вася", 1);
        s.Give(player, objectId);

        var plan = PlanOf(s, player, objectId);
        var use = SeasonEngine.Execute(s.State, new UseItem(s.PlayerId(player), plan.Item.InstanceId), s.Context());

        Assert.Equal(use.Rejection?.Code == RejectionCodes.ItemNoTarget, plan.Refusal == RejectionCodes.ItemNoTarget);
    }

    [Fact]
    public void Nobody_active_for_a_random_target_is_said_before_the_use()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя", "Коля");
        var pack = EconomyScenario.RepositoryPack();
        s.WithContent(pack with { Objects = [.. pack.Objects, .. Selectors()] });
        s.Act(new Engine.Players.SetPlayerInactive(s.PlayerId("Петя"), true));
        s.Act(new Engine.Players.SetPlayerInactive(s.PlayerId("Коля"), true));
        s.Give("Вася", "levy-random");

        Assert.Equal(RejectionCodes.ItemNoTarget, PlanOf(s, "Вася", "levy-random").Refusal);
    }

    // ---- takeObject with pick: chosen — the answers are the objects the engine accepts after the item is spent ----

    private static ObjectDefinition[] Pickers() =>
    [
        Fixture("swap-own", TargetSelector.Self, new TakeObjectAction { Mode = TakeMode.Destroy, Pick = Pick.Chosen, Filter = new ObjectFilterSpec { Kind = ObjectKind.Item } }, excludeSelf: false),
        Fixture("snatch", TargetSelector.Chosen, new TakeObjectAction { Mode = TakeMode.Steal, Pick = Pick.Chosen }),
    ];

    private static Scenario PickersSeason()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя", "Коля");
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with { Objects = [.. pack.Objects, .. Pickers()] });
    }

    [Fact]
    public void An_object_of_ones_own_never_offers_the_item_being_used()
    {
        var s = PickersSeason();
        var swap = s.Give("Коля", "swap-own");
        var orange = s.Give("Коля", "orange");

        var step = Assert.Single(PlanOf(s, "Коля", "swap-own").Steps);

        Assert.Equal(ItemStepKind.PickObject, step.Kind);
        var offered = Assert.Single(step.ObjectsByTarget!).Value;
        Assert.Equal([orange.ToString("N")], (IReadOnlyList<string>)offered);
        Assert.DoesNotContain(swap.ToString("N"), offered);
        foreach (var option in offered)
        {
            var use = SeasonEngine.Execute(s.State, new UseItem(s.PlayerId("Коля"), swap, null, [option]), s.Context());
            Assert.True(use.IsAccepted, use.Rejection?.Detail);
        }
    }

    [Fact]
    public void Every_object_offered_of_a_chosen_target_is_accepted_and_none_is_asked_when_he_has_nothing()
    {
        var s = PickersSeason();
        var snatch = s.Give("Коля", "snatch");
        s.Give("Петя", "orange");
        s.Give("Петя", "banana");

        var plan = PlanOf(s, "Коля", "snatch");
        var step = Assert.Single(plan.Steps);

        Assert.Equal(2, step.ObjectsByTarget![s.PlayerId("Петя")].Count);
        Assert.Empty(step.ObjectsByTarget[s.PlayerId("Вася")]);
        foreach (var (target, options) in step.ObjectsByTarget)
        {
            foreach (var option in options)
            {
                var use = SeasonEngine.Execute(s.State, new UseItem(s.PlayerId("Коля"), snatch, target, [option]), s.Context());
                Assert.True(use.IsAccepted, use.Rejection?.Detail);
            }

            if (options.Count == 0)
            {
                var use = SeasonEngine.Execute(s.State, new UseItem(s.PlayerId("Коля"), snatch, target), s.Context());
                Assert.True(use.IsAccepted, use.Rejection?.Detail);
            }
        }
    }
}
