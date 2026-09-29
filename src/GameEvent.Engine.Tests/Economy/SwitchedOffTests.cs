using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// D-420, invariant 19 (D-22): a mechanic switched off mid-season leaves no event, so what it left waiting — a shop offer,
/// a bet, an effect counting its runs — stays as it is and is not due for the timers; it goes on when the mechanic is back.
/// </summary>
public class SwitchedOffTests
{
    // Silent Hill: 6 h
    private static Scenario Season()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithGame("Gothic", 30, "Horror");
        s.WithPlayers("Вася", "Петя");
        return s.WithContent(EconomyScenario.RepositoryPack()).WithCoins("Вася", 30);
    }

    [Fact]
    public void A_shop_offer_of_a_shop_switched_off_is_not_due_until_the_shop_is_back()
    {
        var s = Season();
        s.RollShop("Вася");
        s.WithRuleset(r => r with { Features = r.Features with { Shop = false } });
        s.Advance(TimeSpan.FromHours(1));

        Assert.Null(Timers.Next(s.Player("Вася"), s.State.Rules.Features));
        s.FireTimers();
        Assert.Equal(RejectionCodes.TimersNothingDue, s.Last.Rejection!.Code);

        s.WithRuleset(r => r with { Features = r.Features with { Shop = true } });
        Assert.NotNull(Timers.Next(s.Player("Вася"), s.State.Rules.Features));
        s.FireTimers();
        Assert.Single(s.LastEvents<ShopOfferExpired>());
    }

    [Fact]
    public void The_shop_price_does_not_start_over_while_the_shop_is_off()
    {
        var s = Season();
        s.RollShop("Вася");
        s.WithRuleset(r => r with { Features = r.Features with { Shop = false } });

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");

        Assert.Empty(s.LastEvents<ShopPriceRestarted>());
        Assert.Equal(1, s.Player("Вася").Wallet.ShopRolls);
    }

    [Fact]
    public void A_bet_is_not_settled_while_bets_are_off()
    {
        var s = Season();
        s.RollTitle("Петя", "Silent Hill").Start("Петя");
        s.Bet("Вася", "Петя", days: 1, stake: 5);
        s.WithRuleset(r => r with { Features = r.Features with { Bets = false } });

        s.NextRandom(1, 1).Complete("Петя");
        Assert.Empty(s.LastEvents<BetSettled>());

        s.Advance(TimeSpan.FromDays(2));
        Assert.Null(Timers.Next(s.Player("Вася"), s.State.Rules.Features));
        s.FireTimers();
        Assert.Equal(RejectionCodes.TimersNothingDue, s.Last.Rejection!.Code);
        Assert.Equal(BetStatus.Open, Assert.Single(s.Player("Вася").Wallet.Bets).Status);

        s.WithRuleset(r => r with { Features = r.Features with { Bets = true } });
        s.FireTimers();
        Assert.Equal(BetStatus.Lost, Assert.Single(s.LastEvents<BetSettled>()).Status);
    }

    [Fact]
    public void An_effect_does_not_count_its_runs_while_items_are_off()
    {
        var s = Season();
        s.Give("Вася", "talisman");
        s.Used("Вася", "talisman");
        var left = s.Held("Вася", "talisman-effect").RunsLeft;
        s.WithRuleset(r => r with { Features = r.Features with { Items = false, Shop = false } });

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");

        Assert.Empty(s.LastEvents<ObjectChanged>());
        Assert.Equal(left, s.Held("Вася", "talisman-effect").RunsLeft);
    }
}
