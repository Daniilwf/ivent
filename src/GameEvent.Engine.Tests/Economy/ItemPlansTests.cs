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
        Fixture("snatch-two", TargetSelector.Chosen, new TakeObjectAction { Mode = TakeMode.Steal, Pick = Pick.Chosen, Count = ContentValue.Of(2) }),
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
    // ---- What the imported pack added (D-470…D-483): the plan offers only what the engine takes ----

    private static Scenario Imported() => ImportedItems.ImportedItemsSeason.New();

    private static ItemPlan PlanOf(Scenario s, string player, Guid instance) =>
        ItemPlans.Of(s.State, Pool(s), s.Clock.UtcNow, s.PlayerId(player)).Single(p => p.Item.InstanceId == instance);

    private static bool Accepted(Scenario s, string player, Guid instance, Guid? target, params string[] answers) =>
        SeasonEngine.Execute(s.State, new UseItem(s.PlayerId(player), instance, target, [.. answers]), s.Context()).IsAccepted;

    [Fact]
    public void A_steal_never_offers_an_object_of_an_excluded_tag_or_one_that_never_changes_hands()
    {
        var s = Imported();
        var rat = s.Give("Вася", "rat");
        var banana = s.Give("Петя", "banana");
        var otherRat = s.Give("Петя", "kind-rat");
        var toughPig = s.Give("Петя", "tough-pig");
        var petya = s.PlayerId("Петя");

        var offered = Assert.Single(PlanOf(s, "Вася", rat).Steps).ObjectsByTarget![petya];

        Assert.Equal([banana.ToString("N")], (IReadOnlyList<string>)offered);
        Assert.True(Accepted(s, "Вася", rat, petya, banana.ToString("N")));
        Assert.False(Accepted(s, "Вася", rat, petya, otherRat.ToString("N")));
        Assert.False(Accepted(s, "Вася", rat, petya, toughPig.ToString("N")));
    }

    [Fact]
    public void A_destruction_never_offers_an_object_that_is_never_destroyed()
    {
        var s = Imported();
        var gremlin = s.Give("Вася", "gremlin");
        var banana = s.Give("Петя", "banana");
        var undestroyable = s.Give("Петя", "schrodinger-rat");
        var petya = s.PlayerId("Петя");

        var offered = Assert.Single(PlanOf(s, "Вася", gremlin).Steps).ObjectsByTarget![petya];

        Assert.Contains(banana.ToString("N"), offered);
        Assert.DoesNotContain(undestroyable.ToString("N"), offered);
        Assert.False(Accepted(s, "Вася", gremlin, petya, undestroyable.ToString("N")));
    }

    [Fact]
    public void A_give_offers_the_users_own_objects_and_with_none_says_nothing_to_give()
    {
        var s = Imported();
        var pig = s.Give("Вася", "pig");
        var petya = s.PlayerId("Петя");

        Assert.Equal(RejectionCodes.ItemNothingToGive, PlanOf(s, "Вася", pig).Refusal);
        Assert.Equal(
            RejectionCodes.ItemNothingToGive,
            SeasonEngine.Execute(s.State, new UseItem(s.PlayerId("Вася"), pig, petya), s.Context()).Rejection?.Code);

        var curse = s.Give("Вася", "curse-effect");
        var plan = PlanOf(s, "Вася", pig);
        var step = Assert.Single(plan.Steps);

        Assert.True(plan.Usable);
        Assert.All(step.ObjectsByTarget!.Values, own => Assert.Equal([curse.ToString("N")], (IReadOnlyList<string>)own));
        Assert.True(Accepted(s, "Вася", pig, petya, curse.ToString("N")));
    }

    [Fact]
    public void A_use_condition_closes_the_item_exactly_while_it_does_not_hold()
    {
        var s = Imported();
        var muffin = s.Give("Вася", "muffin");

        Assert.Equal(RejectionCodes.ItemConditionNotMet, PlanOf(s, "Вася", muffin).Refusal);
        Assert.False(Accepted(s, "Вася", muffin, null));

        s.WithCoins("Вася", 0);

        Assert.True(PlanOf(s, "Вася", muffin).Usable);
        Assert.True(Accepted(s, "Вася", muffin, null));
    }

    [Fact]
    public void A_chosen_direction_asks_forward_or_back()
    {
        var s = Imported();
        var pistol = s.Give("Вася", "pistol");
        var petya = s.PlayerId("Петя");

        var step = Assert.Single(PlanOf(s, "Вася", pistol).Steps);

        Assert.Equal(ItemStepKind.Direction, step.Kind);
        Assert.Equal(["forward", "back"], (IReadOnlyList<string>)step.Options);
        Assert.All(step.Options, way => Assert.True(Accepted(s, "Вася", pistol, petya, way)));
        Assert.False(Accepted(s, "Вася", pistol, petya, "sideways"));
    }

    [Fact]
    public void A_branch_by_choice_asks_its_options()
    {
        var s = Imported();
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var sugar = s.Give("Вася", "sugar-cube");

        var step = Assert.Single(PlanOf(s, "Вася", sugar).Steps);

        Assert.Equal((ItemStepKind.Choice, "Что берёшь?"), (step.Kind, step.Prompt));
        Assert.Equal(["+1d6 к броску", "+3 монетки"], (IReadOnlyList<string>)step.Options);
        Assert.All(step.Options, option => Assert.True(Accepted(s, "Вася", sugar, null, option)));
    }

    [Fact]
    public void A_count_known_only_at_the_use_asks_a_list_of_preference_and_all_asks_nothing()
    {
        var s = Imported();
        var plasma = s.Give("Вася", "plasma-gun");
        var gremlin = s.Give("Вася", "epic-gremlin");
        var first = s.Give("Петя", "banana");
        var second = s.Give("Петя", "orange");
        var petya = s.PlayerId("Петя");

        var step = Assert.Single(PlanOf(s, "Вася", plasma).Steps);

        Assert.True(step.Ranked);
        Assert.Equal([first.ToString("N"), second.ToString("N")], (IReadOnlyList<string>)step.ObjectsByTarget![petya]);
        Assert.True(Accepted(s, "Вася", plasma, petya, second.ToString("N"), first.ToString("N")));
        Assert.True(Accepted(s, "Вася", plasma, petya));
        Assert.Empty(PlanOf(s, "Вася", gremlin).Steps);
    }

    [Fact]
    public void A_fixed_count_chosen_asks_that_many_different_objects()
    {
        var s = PickersSeason();
        var item = s.Give("Коля", "snatch-two");
        var first = s.Give("Петя", "orange");
        var second = s.Give("Петя", "banana");
        var petya = s.PlayerId("Петя");

        var step = Assert.Single(PlanOf(s, "Коля", item).Steps);

        Assert.Equal((2, false), (step.Count, step.Ranked));
        Assert.True(Accepted(s, "Коля", item, petya, first.ToString("N"), second.ToString("N")));
        Assert.False(Accepted(s, "Коля", item, petya, first.ToString("N")));
        Assert.False(Accepted(s, "Коля", item, petya, first.ToString("N"), first.ToString("N")));
    }
}
