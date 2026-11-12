using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Economy.ImportedItems.ImportedItemsSeason;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 6 of the import queue (EC45, D-478): <c>takeObject</c> with <c>mode: give</c> — the user hands one of his own
/// objects to the target (the pigs), refused when he has nothing to give — and the object flags <c>unstealable</c> (never
/// taken, stolen or given) and <c>undestroyable</c> (never destroyed or transformed).
/// </summary>
public class GiveAndFlagsTests
{
    [Fact]
    public void A_pig_hands_the_chosen_effect_to_another_player_as_his_own_doing()
    {
        var s = ImportedItemsSeason.New();
        var curse = s.Give("Вася", "curse-effect");
        s.Give("Вася", "talisman-effect");
        s.Give("Вася", "pig");

        s.Used("Вася", "pig", "Петя", Answer(curse));

        var given = Assert.Single(s.LastEvents<ObjectTransferred>());
        Assert.Equal((s.PlayerId("Вася"), s.PlayerId("Петя"), curse, true), (given.PlayerId, given.ToPlayerId, given.InstanceId, given.Given));
        Assert.Equal(["talisman-effect"], s.Inventory("Вася"));
        var held = s.Held("Петя", "curse-effect");

        // A hostile effect handed over is the user's attack, like one he put on the target (D-425 sees it as such)
        Assert.Equal((true, (Guid?)s.PlayerId("Вася")), (held.Hostile, held.FromPlayerId));
    }

    [Fact]
    public void A_pig_with_nothing_to_give_is_refused()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Вася", "candy");
        var pig = s.Give("Вася", "pig");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pig, "Петя"), RejectionCodes.ItemNothingToGive);
    }

    [Fact]
    public void A_cursed_pig_costs_five_coins_and_an_epic_pig_hands_over_every_effect()
    {
        var s = ImportedItemsSeason.New();
        var curse = s.Give("Вася", "curse-effect");
        s.Give("Вася", "cursed-pig");
        s.Used("Вася", "cursed-pig", "Коля", Answer(curse));
        Assert.Equal(15, s.Player("Вася").Coins);
        Assert.Equal(["curse-effect"], s.Inventory("Коля"));

        s.Give("Вася", "curse-effect");
        s.Give("Вася", "talisman-effect");
        s.Give("Вася", "curse-effect");
        s.Give("Вася", "epic-pig");
        s.Used("Вася", "epic-pig", "Петя");

        // Every hostile effect; the good talisman stays: a pig is no gift to a friend (D-487)
        Assert.Equal(["talisman-effect"], s.Inventory("Вася"));
        Assert.Equal(["curse-effect", "curse-effect"], s.Inventory("Петя"));
    }

    [Theory]
    [InlineData("pig")]
    [InlineData("tough-pig")]
    [InlineData("cursed-pig")]
    [InlineData("epic-pig")]
    public void A_pig_cannot_hand_a_good_effect_to_a_friend(string pigId)
    {
        // SPEC «Проверка на дыры»: «Подкармливать друга … прямых переводов нет» (D-487)
        var s = ImportedItemsSeason.New();
        var talisman = s.Give("Вася", "talisman-effect");
        var shield = s.Give("Вася", "shield-effect");
        var pig = s.Give("Вася", pigId);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pig, "Петя"), RejectionCodes.ItemNothingToGive);
        if (pigId != "epic-pig")
        {
            ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pig, "Петя", Answer(talisman)), RejectionCodes.ItemNothingToGive);
            ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pig, "Петя", Answer(shield)), RejectionCodes.ItemNothingToGive);
        }
    }

    [Fact]
    public void A_pig_with_a_good_and_a_hostile_effect_may_name_only_the_hostile_one()
    {
        var s = ImportedItemsSeason.New();
        var talisman = s.Give("Вася", "talisman-effect");
        s.Give("Вася", "curse-effect");
        var pig = s.Give("Вася", "pig");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pig, "Петя", Answer(talisman)), RejectionCodes.ItemInvalidChoice);
    }

    [Fact]
    public void A_kyukyushka_rat_on_a_player_with_nothing_to_steal_is_refused()
    {
        // It gives first and steals after; a rat item it gives is not stolen back (rat items are not stolen), so on a player
        // with nothing else it would be a plain gift to a friend (D-487)
        var s = ImportedItemsSeason.New();
        var gift = s.Give("Вася", "rat");
        var rat = s.Give("Вася", "kyukyushka-rat");
        s.Give("Петя", "rat");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", rat, "Петя", Answer(gift)), RejectionCodes.ItemNothingToTake);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", rat, "Коля", Answer(gift)), RejectionCodes.ItemNothingToTake);
    }

    [Fact]
    public void A_kind_rat_on_a_player_with_nothing_to_steal_is_refused()
    {
        // The 2 coins and the cell are given «взамен» of a theft: without one they would be a gift to a friend (D-487)
        var s = ImportedItemsSeason.New();
        var rat = s.Give("Вася", "kind-rat");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", rat, "Петя"), RejectionCodes.ItemNothingToTake);

        var pizza = s.Give("Петя", "pizza");
        s.Use("Вася", rat, "Петя", Answer(pizza));
        ScenarioAssert.Accepted(s);
        Assert.Equal(22, s.Player("Петя").Coins);
        Assert.Equal(["pizza"], s.Inventory("Вася"));
    }

    [Fact]
    public void A_pig_reaches_no_finisher()
    {
        var s = WithFinishers();
        var curse = s.Give("Вася", "curse-effect");
        var pig = s.Give("Вася", "pig");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pig, "Коля", Answer(curse)), RejectionCodes.ItemInvalidTarget);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", pig, "Петя", Answer(curse)), RejectionCodes.ItemInvalidTarget);
    }

    [Fact]
    public void A_kyukyushka_rat_gives_one_of_the_users_items_and_steals_one()
    {
        var s = ImportedItemsSeason.New();
        var pizza = s.Give("Петя", "pizza");
        var candy = s.Give("Вася", "candy");
        s.Give("Вася", "kyukyushka-rat");

        s.Used("Вася", "kyukyushka-rat", "Петя", Answer(candy), Answer(pizza));

        Assert.Equal(["pizza"], s.Inventory("Вася"));
        Assert.Equal(["candy"], s.Inventory("Петя"));
    }

    [Fact]
    public void A_kyukyushka_rat_without_an_item_to_give_back_is_refused()
    {
        var s = ImportedItemsSeason.New();
        var pizza = s.Give("Петя", "pizza");
        s.Give("Вася", "curse-effect");
        var rat = s.Give("Вася", "kyukyushka-rat");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", rat, "Петя", Answer(pizza)), RejectionCodes.ItemNothingToGive);
    }

    [Fact]
    public void An_unstealable_pig_is_never_stolen_given_or_destroyed()
    {
        var s = ImportedItemsSeason.New();
        var tough = s.Give("Петя", "tough-pig");
        var rat = s.Give("Вася", "rat");
        var gremlin = s.Give("Вася", "gremlin");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", rat, "Петя", Answer(tough)), RejectionCodes.ItemInvalidChoice);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", gremlin, "Петя", Answer(tough)), RejectionCodes.ItemInvalidChoice);

        // Нечего отдать: the only item Петя holds cannot change hands
        s.Give("Петя", "kyukyushka-rat");
        var candy = s.Give("Вася", "candy");
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Use("Петя", s.Held("Петя", "kyukyushka-rat").InstanceId, "Вася", Answer(tough), Answer(candy)), RejectionCodes.ItemNothingToGive);
    }

    [Fact]
    public void A_tough_pig_hands_over_an_effect_and_is_out_of_reach_of_a_magnet_and_a_rare_gremlin()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Петя", "tough-pig");
        s.Give("Вася", "magnet");
        s.Give("Вася", "rare-gremlin");

        s.Used("Вася", "magnet", "Петя");
        Assert.Empty(s.LastEvents<ObjectTransferred>());
        s.Used("Вася", "rare-gremlin", "Петя");
        Assert.Equal(["tough-pig"], s.Inventory("Петя"));

        var curse = s.Give("Петя", "curse-effect");
        s.Used("Петя", "tough-pig", "Коля", Answer(curse));
        Assert.Equal(["curse-effect"], s.Inventory("Коля"));
    }

    [Fact]
    public void A_schrodinger_rat_steals_and_cannot_be_destroyed_but_can_be_stolen()
    {
        var s = ImportedItemsSeason.New();
        var schrodinger = s.Give("Петя", "schrodinger-rat");
        var gremlin = s.Give("Вася", "gremlin");
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", gremlin, "Петя", Answer(schrodinger)), RejectionCodes.ItemInvalidChoice);

        s.Give("Вася", "rare-gremlin");
        s.Used("Вася", "rare-gremlin", "Петя");
        Assert.Equal(["schrodinger-rat"], s.Inventory("Петя"));

        // A rat does not steal rat items, a magnet steals anything stealable
        s.Give("Вася", "magnet");
        s.Used("Вася", "magnet", "Петя");
        Assert.Equal(["gremlin", "schrodinger-rat"], s.Inventory("Вася"));

        var pizza = s.Give("Коля", "pizza");
        s.Used("Вася", "schrodinger-rat", "Коля", Answer(pizza));
        Assert.Contains("pizza", s.Inventory("Вася"));
    }

    [Fact]
    public void An_undestroyable_object_that_does_not_fit_stays_with_its_owner()
    {
        // Given: Петя's inventory is full, Вася hands him the rat of Schrödinger with a kyukyushka rat and steals a pizza
        var s = ImportedItemsSeason.New();
        var pizza = s.Give("Петя", "pizza");
        s.Give("Петя", "pizza");
        var schrodinger = s.Give("Вася", "schrodinger-rat");
        s.Give("Вася", "kyukyushka-rat");
        s.WithRuleset(r => r with { Economy = r.Economy with { InventoryLimit = 2 } });

        s.Used("Вася", "kyukyushka-rat", "Петя", Answer(schrodinger), Answer(pizza));

        // The rat cannot be destroyed, so it is not lost on the way: it stays with Вася, nothing is lost (D-478, AU4-2)
        Assert.Empty(s.LastEvents<ObjectLost>());
        Assert.DoesNotContain(s.LastEvents<ObjectRemoved>(), r => r.InstanceId == schrodinger);
        Assert.Equal(["schrodinger-rat", "pizza"], s.Inventory("Вася"));
        Assert.Equal(["pizza"], s.Inventory("Петя"));
    }

    [Fact]
    public void An_undestroyable_object_stolen_into_a_full_inventory_stays_with_the_target()
    {
        var s = ImportedItemsSeason.New();
        s.Give("Петя", "schrodinger-rat");
        s.Give("Вася", "pizza");
        s.Give("Вася", "pizza");
        s.Give("Вася", "magnet");
        s.WithRuleset(r => r with { Economy = r.Economy with { InventoryLimit = 2 } });

        s.Used("Вася", "magnet", "Петя");

        Assert.Empty(s.LastEvents<ObjectLost>());
        Assert.Equal(["schrodinger-rat"], s.Inventory("Петя"));
        Assert.Equal(["pizza", "pizza"], s.Inventory("Вася"));
    }

    [Fact]
    public void An_undestroyable_object_is_not_transformed_either()
    {
        var turn = new ObjectDefinition
        {
            Id = "turn-into-candy",
            Kind = ObjectKind.Item,
            Name = "x",
            Description = "x",
            Window = UseWindow.Anytime,
            Effect = new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.Chosen, ExcludeSelf = true },
                Actions = [new TransformObjectAction { Mode = TransformMode.Transform, Into = "candy", Filter = new ObjectFilterSpec { Kind = ObjectKind.Item } }],
            },
        };
        var s = ImportedItemsSeason.New();
        var pack = EconomyScenario.RepositoryPack();
        s.WithContent(pack with { Objects = [.. pack.Objects, turn] });
        s.Give("Петя", "schrodinger-rat");
        s.Give("Вася", "turn-into-candy");

        s.Used("Вася", "turn-into-candy", "Петя");

        Assert.Equal(["schrodinger-rat"], s.Inventory("Петя"));
    }

    [Fact]
    public void Giving_is_to_another_player()
    {
        var pig = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "pig");
        var bad = pig with
        {
            Effect = pig.Effect! with { Actions = [new TakeObjectAction { Mode = TakeMode.Give, Pick = Pick.Chosen, Target = ActionTarget.Self }] },
        };

        Assert.Contains("$.effect.actions[0].target", ContentValidator.Check(bad).Select(e => e.Path));
    }
}
