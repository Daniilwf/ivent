using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Economy.ImportedItems;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// Defence in depth behind the validators (NB-SEC-1, NB-SEC-2; D-651, D-652): the shop price, a bet's payout, reflection and the
/// ledger stay in range even with numbers the validators would refuse today (a ruleset or a pack logged before them).
/// </summary>
public class NumberBoundsTests
{
    private static ShopRules ShopRules(int rollCost, int step) => TestRuleset.Create().Economy.Shop with { RollCost = rollCost, RerollCostStep = step };

    private static SeasonPlayer AfterShopRolls(int rolls)
    {
        var player = EconomyScenario.New().WithPlayers("Вася").Player("Вася");
        return player with { Economy = player.Wallet with { ShopRolls = rolls } };
    }

    [Fact]
    public void A_negative_reroll_step_never_makes_a_shop_roll_pay_the_player()
    {
        var (payment, price) = Shop.NextRoll(AfterShopRolls(3), ShopRules(5, -5));

        Assert.Equal((ShopPayment.Coins, 0), (payment, price));
    }

    [Fact]
    public void A_huge_reroll_step_stops_at_the_largest_price_instead_of_wrapping_around()
    {
        var (_, price) = Shop.NextRoll(AfterShopRolls(1_000), ShopRules(int.MaxValue, int.MaxValue));

        Assert.Equal(int.MaxValue, price);
    }

    [Theory]
    [InlineData(10, 2.5, 25)]
    [InlineData(int.MaxValue, 3.0, int.MaxValue)]
    [InlineData(10, 79228162514264337593543950335.0, int.MaxValue)]
    public void A_bets_payout_is_clamped_to_the_coins_a_player_can_hold(int stake, double multiplier, int payout)
    {
        var bet = new Bet(Guid.Empty, Guid.Empty, Guid.Empty, default, default, stake, multiplier > 1e20 ? decimal.MaxValue : (decimal)multiplier);

        Assert.Equal(payout, Betting.Payout(bet));
    }

    [Fact]
    public void A_reflected_hit_at_the_cap_goes_back_whole_and_keeps_its_sign()
    {
        // The largest damage content may take (a million) times the largest multiplier of reflection (ten)
        var grenade = EconomyScenario.RepositoryPack().Objects.Single(o => o.Id == "grenade");
        var nuke = grenade with
        {
            Id = "nuke",
            Effect = grenade.Effect! with
            {
                Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(-ContentValidator.MaxResourceAmount), Reflect = true }],
            },
        };
        Assert.Empty(ContentValidator.Check(nuke));
        var s = ImportedItemsSeason.New()
            .WithRuleset(r => r with { Effects = r.Effects with { Reflect = new CoinReflect { CoinsAtMost = -30, Multiplier = 10 } } })
            .WithContent(new ContentPack { Objects = [.. EconomyScenario.RepositoryPack().Objects, nuke], Wheels = EconomyScenario.RepositoryPack().Wheels })
            .WithCoins("Петя", -30);
        s.Give("Вася", "nuke");

        s.Used("Вася", "nuke", target: "Петя");

        Assert.Equal((-30, 20 - 10_000_000), (s.Player("Петя").Coins, s.Player("Вася").Coins));
        Assert.Equal(-10_000_000, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void The_ledger_stops_at_the_ends_of_a_number_instead_of_changing_sign()
    {
        var s = EconomyScenario.New().WithPlayers("Вася").WithCoins("Вася", -20);
        var vasya = s.PlayerId("Вася");

        var poorer = PointsLedger.Apply(s.State, new CoinsChanged(vasya, int.MinValue, CoinsReason.Item, RunId: null));
        var richer = PointsLedger.Apply(PointsLedger.Apply(s.State, new PointsChanged(vasya, int.MaxValue, PointsReason.Item, RunId: null)), new PointsChanged(vasya, int.MaxValue, PointsReason.Item, RunId: null));

        Assert.Equal(int.MinValue, poorer.Players[vasya].Coins);
        Assert.Equal(int.MaxValue, richer.Players[vasya].Points);
    }

    [Fact]
    public void A_resource_stops_at_the_ends_of_a_number_instead_of_changing_sign()
    {
        var bag = Engine.Kernel.ResourceBag.Empty.Add("freeShopRolls", int.MaxValue).Add("freeShopRolls", 5);

        Assert.Equal(int.MaxValue, bag["freeShopRolls"]);
    }
}
