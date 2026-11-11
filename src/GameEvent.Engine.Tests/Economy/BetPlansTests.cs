using GameEvent.Engine.Economy;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC23 (D-446): what the bets screen offers is what <c>PlaceBet</c> accepts — the runs of other players rolled within the
/// window, not the first finisher's, not one already bet on; the multiplier of each deadline; the refusal when no bet is
/// placed at all (the limit of open bets, debt, no coins, the mechanic off).
/// </summary>
public class BetPlansTests
{
    // Silent Hill: 6 h; Gothic: 30 h
    private static Scenario Season()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Gothic", 30, "Horror").WithGame("Tetris", null, "Horror");
        s.WithPlayers("Вася", "Петя", "Коля");
        s.WithCoins("Вася", 20).WithCoins("Коля", 20);
        s.RollTitle("Петя", "Silent Hill").Start("Петя");
        return s;
    }

    private static BetPlan Plan(Scenario s, string player) => BetPlans.Of(s.State, s.Clock.UtcNow, s.PlayerId(player))!;

    [Fact]
    public void The_runs_offered_are_the_runs_a_bet_is_accepted_on_with_their_multipliers()
    {
        var s = Season();
        var plan = Plan(s, "Вася");

        var run = Assert.Single(plan.Runs);
        Assert.Equal((s.PlayerId("Петя"), 6m, s.Clock.UtcNow.AddHours(24)), (run.PlayerId, run.Hours, run.ClosesAt));
        Assert.Equal([new BetOption(1, 3.0m), new BetOption(3, 2.0m), new BetOption(7, 1.2m)], run.Options);
        Assert.Equal((null, 0, 3, 10), (plan.Refusal, plan.OpenBets, plan.MaxOpenBets, plan.MaxStake));

        foreach (var option in run.Options)
        {
            var again = Season();
            again.Bet("Вася", "Петя", option.Days, 1);
            Assert.Equal(option.Multiplier, Assert.Single(again.LastEvents<BetPlaced>()).Bet.Multiplier);
        }
    }

    [Fact]
    public void A_run_is_not_offered_to_its_player_once_bet_on_or_after_the_window()
    {
        var s = Season();
        Assert.DoesNotContain(Plan(s, "Петя").Runs, r => r.PlayerId == s.PlayerId("Петя"));

        s.Bet("Вася", "Петя", 3, 1);
        Assert.Empty(Plan(s, "Вася").Runs);
        Assert.Equal(1, Plan(s, "Вася").OpenBets);
        Assert.Single(Plan(s, "Коля").Runs);

        s.Advance(TimeSpan.FromHours(24));
        Assert.Single(Plan(s, "Коля").Runs);
        s.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(Plan(s, "Коля").Runs);
        s.Bet("Коля", "Петя", 3, 1);
        Assert.Equal(RejectionCodes.BetWindowClosed, s.Last.Rejection!.Code);
    }

    [Theory]
    [InlineData("tooMany", RejectionCodes.BetTooMany)]
    [InlineData("debt", RejectionCodes.CoinsInDebt)]
    [InlineData("zero", RejectionCodes.NotEnoughCoins)]
    [InlineData("off", RejectionCodes.FeatureDisabled)]
    public void No_bet_at_all_is_shown_refused_as_the_engine_refuses_it(string what, string code)
    {
        var s = Season();
        switch (what)
        {
            case "tooMany": s.WithRuleset(r => r with { Bets = r.Bets with { MaxOpenBetsPerPlayer = 1 } }); s.RollTitle("Коля", "Gothic").Start("Коля"); s.Bet("Вася", "Коля", 3, 1); break;
            case "debt": s.WithCoins("Вася", -1); break;
            case "zero": s.WithCoins("Вася", 0); break;
            case "off": s.WithRuleset(r => r with { Features = r.Features with { Bets = false } }); break;
            default: break;
        }

        var plan = Plan(s, "Вася");
        s.Bet("Вася", "Петя", 3, 1);

        Assert.Equal(code, plan.Refusal);
        Assert.Empty(plan.Runs);
        Assert.Equal(code, s.Last.Rejection!.Code);
    }

    [Fact]
    public void The_largest_stake_is_the_limit_or_the_coins_held_and_a_stranger_has_no_plan()
    {
        var s = Season().WithCoins("Вася", 4);

        Assert.Equal(4, Plan(s, "Вася").MaxStake);
        Assert.Null(BetPlans.Of(s.State, s.Clock.UtcNow, Guid.NewGuid()));
    }

    [Fact]
    public void The_first_finisher_is_not_offered()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Gothic", 30, "Horror").WithMapLength(2).WithPlayers("Вася", "Петя");
        s.WithCoins("Вася", 20);
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        s.RollTitle("Петя", "Gothic").Start("Петя");

        Assert.Empty(Plan(s, "Вася").Runs);
    }
}
