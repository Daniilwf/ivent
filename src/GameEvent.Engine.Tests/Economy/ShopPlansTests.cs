using GameEvent.Engine.Economy;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC22 (D-443): what the shop screen offers is what the engine does — the next roll's price and payment, the refusal a
/// roll gets now (debt, too few coins, the mechanic off), and the offer only while it lasts by the server's clock.
/// </summary>
public class ShopPlansTests
{
    private static Scenario Season() =>
        EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя")
            .WithContent(EconomyScenario.ExamplePack());

    [Fact]
    public void The_price_shown_is_the_price_paid_and_rises_with_each_roll()
    {
        var s = Season().WithCoins("Вася", 30);
        var before = ShopPlans.Of(s.State, s.Clock.UtcNow, s.PlayerId("Вася"))!;

        s.RollShop("Вася");

        var rolled = Assert.Single(s.LastEvents<ShopRolled>());
        Assert.Equal((ShopPayment.Coins, rolled.Price), (before.Payment, before.Price));
        var after = ShopPlans.Of(s.State, s.Clock.UtcNow, s.PlayerId("Вася"))!;
        Assert.True(after.Price > before.Price);
        Assert.Equal(rolled.Lots, after.Offer!.Lots);
        Assert.Null(after.Refusal);
    }

    [Fact]
    public void An_offer_past_its_time_is_gone_before_the_timer_fires()
    {
        var s = Season().WithCoins("Вася", 30);
        s.RollShop("Вася");

        s.Advance(TimeSpan.FromMinutes(15));

        Assert.NotNull(s.Player("Вася").Wallet.Shop);
        Assert.Null(ShopPlans.Of(s.State, s.Clock.UtcNow, s.PlayerId("Вася"))!.Offer);
    }

    [Theory]
    [InlineData(-1, RejectionCodes.CoinsInDebt)]
    [InlineData(2, RejectionCodes.NotEnoughCoins)]
    public void A_roll_the_engine_refuses_is_shown_refused(int coins, string code)
    {
        var s = Season().WithCoins("Вася", coins);
        var plan = ShopPlans.Of(s.State, s.Clock.UtcNow, s.PlayerId("Вася"))!;

        s.RollShop("Вася");

        Assert.Equal(code, plan.Refusal);
        Assert.Equal(code, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_free_roll_costs_nothing_and_the_shop_off_is_refused()
    {
        var s = Season();
        s.Act(new Engine.Players.AdjustPlayer(s.PlayerId("Вася"), "Купон", ResourceDeltas: [new Engine.Players.ResourceDelta("freeShopRolls", 1)]));
        var plan = ShopPlans.Of(s.State, s.Clock.UtcNow, s.PlayerId("Вася"))!;
        Assert.Equal((ShopPayment.FreeRoll, 0, 1, null), (plan.Payment, plan.Price, plan.FreeRolls, plan.Refusal));

        s.WithRuleset(r => r with { Features = r.Features with { Shop = false } });

        Assert.Equal(RejectionCodes.FeatureDisabled, ShopPlans.Of(s.State, s.Clock.UtcNow, s.PlayerId("Вася"))!.Refusal);
        Assert.Null(ShopPlans.Of(s.State, s.Clock.UtcNow, Guid.NewGuid()));
    }
}
