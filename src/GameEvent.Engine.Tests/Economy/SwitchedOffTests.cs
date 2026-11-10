using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// D-420, invariant 19 (D-22): a mechanic switched off mid-season leaves no event, so what it left waiting — a shop offer,
/// an effect counting its runs — stays as it is and is not due for the timers; it goes on when the mechanic is back. Bets
/// are not switched off while one is in play: open, or won on a run whose proof may still be rejected.
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
    public void Bets_are_not_switched_off_while_a_bet_is_open()
    {
        var s = Season();
        s.RollTitle("Петя", "Silent Hill").Start("Петя");
        s.Bet("Вася", "Петя", days: 1, stake: 5);

        s.ExpectRejection().Act(new Engine.Rulesets.ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { Bets = false } }));

        Assert.Equal(RejectionCodes.BetsInPlay, s.Last.Rejection!.Code);
        Assert.True(s.State.Rules.Features.Bets);
    }

    [Fact]
    public void Bets_are_not_switched_off_while_a_won_bet_waits_for_the_proof()
    {
        // A reject of that run must still take the win back (D-327)
        var s = Season();
        s.RollTitle("Петя", "Silent Hill").Start("Петя");
        s.Bet("Вася", "Петя", days: 1, stake: 5);
        s.NextRandom(1, 1).Complete("Петя");
        var run = Assert.Single(s.Player("Вася").Wallet.Bets).RunId;

        s.ExpectRejection().Act(new Engine.Rulesets.ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { Bets = false } }));
        Assert.Equal(RejectionCodes.BetsInPlay, s.Last.Rejection!.Code);

        s.Act(new Engine.Proofs.ApproveProof(run, null, "Видел стрим"));
        Assert.True(s.Last.IsAccepted, s.Last.Rejection?.ToString());
        s.WithRuleset(r => r with { Features = r.Features with { Bets = false } });
        Assert.False(s.State.Rules.Features.Bets);
    }

    [Fact]
    public void Bets_settled_or_lost_let_bets_be_switched_off()
    {
        var s = Season();
        s.RollTitle("Петя", "Silent Hill").Start("Петя");
        s.Bet("Вася", "Петя", days: 1, stake: 5);
        s.Advance(TimeSpan.FromDays(1));
        s.FireTimers();

        s.WithRuleset(r => r with { Features = r.Features with { Bets = false } });

        Assert.False(s.State.Rules.Features.Bets);
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

    [Fact]
    public void An_items_filter_of_the_roll_in_progress_stops_with_items_off()
    {
        // Over the seeds, a reroll after items are switched off lands on the long games again
        var longAfter = 0;
        foreach (var seed in Enumerable.Range(1, 15))
        {
            var s = EconomyScenario.New(seed).WithCategory("Horror")
                .WithGame("Limbo", 3, "Horror").WithGame("Inside", 9, "Horror").WithGame("Witcher", 50, "Horror")
                .WithGame("Skyrim", 30, "Horror").WithGame("Persona", 80, "Horror").WithGame("Yakuza", 40, "Horror");
            s.WithPlayers("Вася").WithContent(EconomyScenario.RepositoryPack());
            s.Give("Вася", "short-game");
            s.Used("Вася", "short-game").Roll("Вася");
            Assert.True(s.Player("Вася").Offer!.Snapshot.Hours < 10);
            s.WithRuleset(r => r with { Features = r.Features with { Items = false, Shop = false } });

            s.Act(new Engine.Rolls.Reroll(s.PlayerId("Вася")));

            ScenarioAssert.Accepted(s);
            longAfter += s.Player("Вася").Offer!.Snapshot.Hours >= 10 ? 1 : 0;
        }

        Assert.True(longAfter > 0, "With items off, the item's filter still held the reroll.");
    }
}
