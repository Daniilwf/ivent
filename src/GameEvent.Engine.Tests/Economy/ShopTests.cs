using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC4, EC5: the shop (SPEC «Магазин», D-403, D-404) — personal lots by rarity weight, the price of a roll growing within one
/// game and starting over when a run ends, the coupon, lots that vanish by the server's clock, no purchases in debt, the
/// inventory limit, zone prices; and the timers command.
/// </summary>
public class ShopTests
{
    private static Scenario Season(ContentPack? pack = null)
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithPlayers("Вася", "Петя");
        return s.WithContent(pack ?? EconomyScenario.ExamplePack());
    }

    // Items for sale in the example pack, by id: common — bird-thief 15, lucky-die 15, orange 10, reroll-coupon 12,
    // short-game 20; epic — dirty-trick 30, pick-of-three 25, reroll-dice 30, shield 25; legendary — curse 45.
    [Fact]
    public void A_roll_gives_three_personal_lots_by_rarity_for_fifteen_minutes()
    {
        var s = Season().WithCoins("Вася", 20);

        // rarity tickets 0 (common), 70 (epic), 95 (legendary); then the item within the rarity
        s.NextRandom(0, 2, 70, 0, 95, 0).RollShop("Вася");

        var rolled = Assert.Single(s.LastEvents<ShopRolled>());
        Assert.Equal(
            [new ShopLot("orange", Rarity.Common, 10), new ShopLot("dirty-trick", Rarity.Epic, 30), new ShopLot("curse", Rarity.Legendary, 45)],
            rolled.Lots);
        Assert.Equal((ShopPayment.Coins, 5, s.Clock.UtcNow.AddMinutes(15)), (rolled.Payment, rolled.Price, rolled.ExpiresAt));
        Assert.Equal(-5, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
        Assert.Equal(15, s.Player("Вася").Coins);
        Assert.Equal(rolled.Lots, s.Player("Вася").Wallet.Shop!.Lots);
        Assert.Empty(s.Player("Петя").Wallet.Inventory);
    }

    [Fact]
    public void Lots_of_one_roll_do_not_repeat_while_there_are_others()
    {
        var s = Season(EconomyScenario.ExamplePack("orange", "reroll-coupon")).WithCoins("Вася", 20);

        s.NextRandom(0, 0, 0, 0).RollShop("Вася");

        Assert.Equal(["orange", "reroll-coupon"], Assert.Single(s.LastEvents<ShopRolled>()).Lots.Select(l => l.ObjectId));
    }

    [Fact]
    public void Nothing_for_sale_refuses_the_roll()
    {
        var s = Season(EconomyScenario.ExamplePack("shield-effect", "curse-effect")).WithCoins("Вася", 20);

        s.RollShop("Вася");

        Assert.Equal(RejectionCodes.ShopEmpty, s.Last.Rejection!.Code);
        Assert.Equal(20, s.Player("Вася").Coins);
    }

    [Fact]
    public void Every_roll_within_a_game_costs_more_and_the_price_starts_over_after_a_completion()
    {
        var s = Season().WithCoins("Вася", 100);

        s.RollShop("Вася").RollShop("Вася").RollShop("Вася");
        Assert.Equal([5, 10, 15], s.Log.OfType<ShopRolled>().Select(r => r.Price));
        Assert.Equal(3, s.Player("Вася").Wallet.ShopRolls);

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");
        Assert.Single(s.LastEvents<ShopPriceRestarted>());
        s.RollShop("Вася");
        Assert.Equal(5, Assert.Single(s.LastEvents<ShopRolled>()).Price);
    }

    [Fact]
    public void A_drop_starts_the_price_over_too()
    {
        var s = Season().WithCoins("Вася", 100);
        s.RollShop("Вася");
        s.RollTitle("Вася", "Silent Hill").Start("Вася");

        s.NextRandom(1, 1).Act(new Engine.Runs.DropRun(s.PlayerId("Вася")));

        Assert.Single(s.LastEvents<ShopPriceRestarted>());
        Assert.Equal(0, s.Player("Вася").Wallet.ShopRolls);
    }

    [Fact]
    public void The_price_does_not_start_over_on_what_the_rules_leave_out()
    {
        var s = Season().WithCoins("Вася", 100);
        s.WithRuleset(r => r with { Economy = r.Economy with { Shop = r.Economy.Shop with { ResetOn = [Engine.Rulesets.ShopPriceReset.RunDropped] } } });
        s.RollShop("Вася");

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");

        Assert.Empty(s.LastEvents<ShopPriceRestarted>());
        Assert.Equal(1, s.Player("Вася").Wallet.ShopRolls);
    }

    [Fact]
    public void A_coupon_gives_a_free_roll_spent_before_coins()
    {
        var s = Season(EconomyScenario.RepositoryPack()).WithCoins("Вася", 0);
        s.Give("Вася", "shop-coupon");
        s.Used("Вася", "shop-coupon");
        Assert.Equal(1, s.Player("Вася").Resources[Shop.FreeShopRollsResource]);

        s.RollShop("Вася");

        var rolled = Assert.Single(s.LastEvents<ShopRolled>());
        Assert.Equal((ShopPayment.FreeRoll, 0), (rolled.Payment, rolled.Price));
        Assert.Equal((Shop.FreeShopRollsResource, -1, ResourceReason.ShopRoll), (s.LastEvents<ResourceChanged>().Single().Resource, s.LastEvents<ResourceChanged>().Single().Delta, s.LastEvents<ResourceChanged>().Single().Reason));
        Assert.Empty(s.LastEvents<CoinsChanged>());
    }

    [Fact]
    public void Buying_a_lot_gives_the_item_and_takes_its_price()
    {
        var s = Season().WithCoins("Вася", 30);
        s.NextRandom(0, 2, 0, 0, 0, 0).RollShop("Вася");

        s.Buy("Вася", 0);

        var bought = Assert.Single(s.LastEvents<LotBought>());
        Assert.Equal(("orange", 10), (bought.Item.ObjectId, bought.Price));
        Assert.Equal((-10, CoinsReason.Purchase), (s.LastEvents<CoinsChanged>().Single().Delta, s.LastEvents<CoinsChanged>().Single().Reason));
        Assert.Equal(["orange"], s.Inventory("Вася"));
        Assert.True(s.Player("Вася").Wallet.Shop!.Lots[0].Sold);

        s.Buy("Вася", 0);
        Assert.Equal(RejectionCodes.ShopLotSold, s.Last.Rejection!.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Only_a_lot_of_the_offer_is_bought(int lot)
    {
        var s = Season().WithCoins("Вася", 30);
        s.RollShop("Вася");

        s.Buy("Вася", lot);

        Assert.Equal(RejectionCodes.ShopUnknownLot, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Nothing_is_bought_without_an_offer()
    {
        var s = Season().WithCoins("Вася", 30);

        s.Buy("Вася", 0);

        Assert.Equal(RejectionCodes.ShopNoOffer, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Lots_vanish_by_the_servers_clock_even_before_the_timer_fires()
    {
        var s = Season().WithCoins("Вася", 30);
        s.RollShop("Вася");

        s.Advance(TimeSpan.FromMinutes(15));
        s.Buy("Вася", 0);
        Assert.Equal(RejectionCodes.ShopOfferExpired, s.Last.Rejection!.Code);

        s.FireTimers();
        Assert.Single(s.LastEvents<ShopOfferExpired>());
        Assert.Null(s.Player("Вася").Wallet.Shop);
        Assert.Null(Timers.Next(s.Player("Вася")));
    }

    [Fact]
    public void A_lot_is_bought_up_to_the_last_moment()
    {
        var s = Season().WithCoins("Вася", 30);
        s.RollShop("Вася");

        s.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        s.Buy("Вася", 0);

        Assert.True(s.Last.IsAccepted);
    }

    [Fact]
    public void Timers_with_nothing_due_are_refused_and_write_nothing()
    {
        var s = Season().WithCoins("Вася", 30);
        s.RollShop("Вася");

        s.FireTimers();

        Assert.Equal(RejectionCodes.TimersNothingDue, s.Last.Rejection!.Code);
        Assert.Equal(s.Clock.UtcNow.AddMinutes(15), Timers.Next(s.Player("Вася")));
    }

    [Fact]
    public void Timers_run_while_the_season_runs_or_closes_only()
    {
        var s = EconomyScenario.New().AsDraft().WithPlayers("Вася");

        s.FireTimers();
        Assert.Equal(RejectionCodes.SeasonClosed, s.Last.Rejection!.Code);

        var none = Scenario.New();
        none.FireTimers();
        Assert.Equal(RejectionCodes.SeasonNotCreated, none.Last.Rejection!.Code);
    }

    [Fact]
    public void Nothing_is_bought_in_debt_even_with_the_price_at_hand()
    {
        var s = Season().WithCoins("Вася", 30);
        s.RollShop("Вася");
        s.WithCoins("Вася", -1);

        s.Buy("Вася", 0);
        Assert.Equal(RejectionCodes.CoinsInDebt, s.Last.Rejection!.Code);

        s.RollShop("Вася");
        Assert.Equal(RejectionCodes.CoinsInDebt, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Not_enough_coins_refuses_the_roll_and_the_purchase()
    {
        var s = Season().WithCoins("Вася", 4);

        s.RollShop("Вася");
        Assert.Equal(RejectionCodes.NotEnoughCoins, s.Last.Rejection!.Code);

        s.WithCoins("Вася", 5).NextRandom(0, 2, 0, 0, 0, 0).RollShop("Вася");
        s.Buy("Вася", 0);
        Assert.Equal(RejectionCodes.NotEnoughCoins, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_full_inventory_refuses_a_purchase()
    {
        var s = Season().WithCoins("Вася", 30);
        foreach (var _ in Enumerable.Range(0, 5))
        {
            s.Give("Вася", "orange");
        }

        s.RollShop("Вася");
        s.Buy("Вася", 0);

        Assert.Equal(RejectionCodes.InventoryFull, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_zone_changes_the_prices_of_its_shop()
    {
        var zone = new ZoneDefinition { Id = "market", Name = "Рынок", ShopPriceMultiplier = 1.5m };
        var map = MapBuilder.New().Path("start", "a", "finish").Zone(zone, "start").Build();
        var s = EconomyScenario.New().WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");
        s.WithContent(EconomyScenario.ExamplePack("orange", "reroll-coupon")).WithCoins("Вася", 30);

        s.NextRandom(0, 0, 0, 0).RollShop("Вася");

        Assert.Equal([15, 18], Assert.Single(s.LastEvents<ShopRolled>()).Lots.Select(l => l.Price));
    }

    [Fact]
    public void A_rarity_of_weight_zero_is_never_offered()
    {
        var s = Season(EconomyScenario.ExamplePack("orange", "curse", "curse-effect")).WithCoins("Вася", 30);
        s.WithRuleset(r => r with { Economy = r.Economy with { RarityWeights = r.Economy.RarityWeights with { Legendary = 0 } } });

        s.RollShop("Вася");

        Assert.Equal(["orange"], Assert.Single(s.LastEvents<ShopRolled>()).Lots.Select(l => l.ObjectId));
    }

    [Fact]
    public void The_shop_is_refused_while_its_flag_is_off_and_to_the_first_finisher()
    {
        var s = Season().WithCoins("Вася", 30);
        s.WithRuleset(r => r with { Features = r.Features with { Shop = false } });

        s.RollShop("Вася");
        Assert.Equal(RejectionCodes.FeatureDisabled, s.Last.Rejection!.Code);
        Assert.Empty(s.Last.Events);

        var f = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithMapLength(2).WithPlayers("Вася");
        f.WithContent(EconomyScenario.ExamplePack()).WithCoins("Вася", 30);
        f.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");
        f.RollShop("Вася");
        Assert.Equal(RejectionCodes.InventoryFrozen, f.Last.Rejection!.Code);
    }

    [Fact]
    public void A_removed_object_of_a_timer_expires()
    {
        var hourly = new ObjectDefinition
        {
            Id = "hourly",
            Kind = ObjectKind.Effect,
            Name = "На час",
            Description = "Живёт час.",
            Rarity = Rarity.Common,
            Effect = new EffectSpec { Trigger = Trigger.AfterRoll, Duration = new DurationSpec { Hours = 1 }, Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(1) }] },
        };
        var s = Season(EconomyScenario.ExamplePack() with { Objects = [.. EconomyScenario.ExamplePack().Objects, hourly] });
        s.Give("Вася", "hourly");
        Assert.Equal(s.Clock.UtcNow.AddHours(1), s.Held("Вася", "hourly").ExpiresAt);

        s.Advance(TimeSpan.FromHours(1));
        s.FireTimers();

        Assert.Equal(ObjectRemoval.Expired, Assert.Single(s.LastEvents<ObjectRemoved>()).Reason);
        Assert.Empty(s.Inventory("Вася"));
    }
}
