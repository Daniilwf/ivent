using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Economy.ImportedItems.ImportedItemsSeason;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// Step 2 of the import queue (EC41, D-474): <c>changeResource.reflect</c> — coin damage to a target with at most
/// <c>effects.reflect.coinsAtMost</c> coins goes to the one who used the item, multiplied by <c>effects.reflect.multiplier</c>;
/// the weapons of the pack (grenades and dynamite) and the tomahawk.
/// </summary>
public class ReflectTests
{
    private static Scenario Season(int? mapLength = null) =>
        ImportedItemsSeason.New(mapLength: mapLength)
            .WithRuleset(r => r with { Effects = r.Effects with { Reflect = new CoinReflect { CoinsAtMost = -30, Multiplier = 2 } } });

    [Theory]
    [InlineData("grenade", -1, -2)]
    [InlineData("better-grenade", -2, -3)]
    [InlineData("rare-grenade", -3, -4)]
    [InlineData("epic-grenade", -4, -5)]
    [InlineData("dynamite", 1, -2)]
    [InlineData("better-dynamite", 2, -3)]
    [InlineData("rare-dynamite", 3, -4)]
    [InlineData("epic-dynamite", 4, -5)]
    public void A_weapon_moves_the_chosen_player_and_takes_his_coins(string weapon, int steps, int coins)
    {
        var s = Season().At("Петя", "c10");
        s.Give("Вася", weapon);

        s.Used("Вася", weapon, target: "Петя");

        Assert.Equal(($"c{10 + steps}", 20 + coins), (s.Player("Петя").CellId, s.Player("Петя").Coins));
        Assert.Equal(20, s.Player("Вася").Coins);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((steps, MoveReason.Item), (moved.Steps, moved.Reason));
        Assert.Equal(1, s.Player("Петя").Wallet.HostileReceived);
    }

    [Theory]
    [InlineData(-30, true)]
    [InlineData(-45, true)]
    [InlineData(-29, false)]
    public void Coin_damage_to_a_target_at_the_threshold_goes_back_doubled(int targetCoins, bool reflected)
    {
        var s = Season().At("Петя", "c10").WithCoins("Петя", targetCoins);
        s.Give("Вася", "rare-grenade");

        s.Used("Вася", "rare-grenade", target: "Петя");

        // The push still happens; only the coins change hands
        Assert.Equal("c7", s.Player("Петя").CellId);
        Assert.Equal(reflected ? (targetCoins, 12) : (targetCoins - 4, 20), (s.Player("Петя").Coins, s.Player("Вася").Coins));
        var change = Assert.Single(s.LastEvents<CoinsChanged>());
        Assert.Equal(reflected ? (s.PlayerId("Вася"), -8) : (s.PlayerId("Петя"), -4), (change.PlayerId, change.Delta));
        Assert.Equal(CoinsReason.Item, change.Reason);
    }

    [Fact]
    public void Without_the_rule_in_the_ruleset_nothing_is_reflected()
    {
        // A season created before the rule existed keeps playing as it did (D-50)
        var s = ImportedItemsSeason.New().WithCoins("Петя", -40);
        Assert.Null(s.State.Rules.Effects.Reflect);
        s.Give("Вася", "grenade");

        s.Used("Вася", "grenade", target: "Петя");

        Assert.Equal((-42, 20), (s.Player("Петя").Coins, s.Player("Вася").Coins));
    }

    [Fact]
    public void Reflected_damage_takes_at_most_what_the_user_has_when_coins_do_not_go_below_zero()
    {
        var s = Season().WithCoins("Петя", -30).WithCoins("Вася", 3);
        s.WithRuleset(r => r with { Economy = r.Economy with { AllowNegativeCoins = false } });
        s.Give("Вася", "grenade");

        s.Used("Вася", "grenade", target: "Петя");

        Assert.Equal((-30, 0), (s.Player("Петя").Coins, s.Player("Вася").Coins));
    }

    [Fact]
    public void A_shield_stops_the_weapon_before_anything_is_reflected()
    {
        var s = Season().WithCoins("Петя", -40);
        s.Give("Петя", "shield");
        s.Used("Петя", "shield");
        s.Give("Вася", "epic-grenade");

        s.Used("Вася", "epic-grenade", target: "Петя");

        Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.Equal((-40, 20, "start"), (s.Player("Петя").Coins, s.Player("Вася").Coins, s.Player("Петя").CellId));
    }

    [Fact]
    public void Dynamite_never_pushes_a_player_onto_the_finish()
    {
        // The finish is reached only by a run's own move (D-322): the push stops a cell before it
        var s = Season(mapLength: 6).At("Петя", "c4");
        s.Give("Вася", "epic-dynamite");

        s.Used("Вася", "epic-dynamite", target: "Петя");

        Assert.Equal("c5", s.Player("Петя").CellId);
        Assert.Null(s.Player("Петя").Finish);
    }

    [Fact]
    public void A_weapon_reaches_neither_the_first_nor_a_later_finisher()
    {
        var s = WithFinishers();
        var grenade = s.Give("Вася", "grenade");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", grenade, "Коля"), RejectionCodes.ItemInvalidTarget);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Вася", grenade, "Петя"), RejectionCodes.ItemInvalidTarget);
    }

    [Theory]
    [InlineData(-29)]
    [InlineData(-30)]
    public void A_tomahawk_takes_its_d12_in_coins_and_cells_and_is_never_reflected(int targetCoins)
    {
        // Its text says nothing of reflection (D-474): the damage stays with the target whatever his coins
        var s = Season().At("Петя", "c20").WithCoins("Петя", targetCoins);
        s.Give("Вася", "legendary-tomahawk");

        s.NextRandom(7).Used("Вася", "legendary-tomahawk", target: "Петя");

        Assert.Equal(12, Assert.Single(Assert.Single(s.LastEvents<EffectRolled>()).Dice).Sides);
        Assert.Equal("c13", s.Player("Петя").CellId);
        Assert.Equal((targetCoins - 7, 20), (s.Player("Петя").Coins, s.Player("Вася").Coins));
    }

    [Theory]
    [InlineData("points", null)]
    [InlineData("coins", ActionTarget.Self)]
    public void Reflection_is_of_coins_taken_from_the_effects_target(string resource, ActionTarget? target)
    {
        var grenade = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "grenade");
        var bad = grenade with
        {
            Effect = grenade.Effect! with
            {
                Actions = [new ChangeResourceAction { Resource = resource, Amount = ContentValue.Of(-2), Reflect = true, Target = target }],
            },
        };

        Assert.Contains("$.effect.actions[0].reflect", ContentValidator.Check(bad).Select(e => e.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void The_multiplier_of_reflection_is_at_least_one(int multiplier)
    {
        var rules = TestRuleset.Create();
        var bad = rules with { Effects = rules.Effects with { Reflect = new CoinReflect { CoinsAtMost = -30, Multiplier = multiplier } } };

        Assert.Contains(RulesetValidator.Validate(bad), e => e.Path == "effects.reflect.multiplier");
    }

    [Fact]
    public void The_default_ruleset_reflects_at_minus_thirty_doubled()
    {
        var reflect = RulesetJson.Default().Effects.Reflect!;

        Assert.Equal((-30, 2), (reflect.CoinsAtMost, reflect.Multiplier));
    }
}
