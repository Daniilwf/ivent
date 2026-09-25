using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// Coins for a completed run (W10; SPEC «Экономика»: «прохождение — чем длиннее игра, тем больше»; Q-2, D-96):
/// <c>max(coins.min, ⌊min(hours, diceCount.max × hoursPerDie) × coins.perHour⌋)</c> by the counted hours, capped by the
/// dice ceiling (D-96 (2)), from the snapshot at the roll, as <c>CoinsChanged(+, CompletionReward, RunId)</c> after the
/// points and the move. Test ruleset: perHour 1, min 3, diceCount max 10 × hoursPerDie 3 → ceiling 30 hours.
/// </summary>
public class CoinsRewardTests
{
    private static Scenario Playing(decimal? hours, Func<Ruleset, Ruleset>? ruleset = null)
    {
        var s = Scenario.New();
        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        s.WithCategory("Horror").WithGame("Silent Hill", hours, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static Func<Ruleset, Ruleset> Coins(decimal perHour, int min) =>
        r => r with { Reward = r.Reward with { Coins = new CoinReward { PerHour = perHour, Min = min } } };

    [Fact]
    public void Roll_snapshot_fixes_the_coin_reward()
    {
        var s = Scenario.New(ruleset: Coins(2, 5)(TestRuleset.Create()))
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася");

        Assert.Equal(new CoinReward { PerHour = 2, Min = 5 }, s.Player("Вася").Offer!.Snapshot.Coins);
    }

    [Theory]
    [InlineData(12.0, 12)]
    [InlineData(3.0, 3)]
    [InlineData(4.0, 4)]
    [InlineData(7.5, 7)] // floor, not rounding
    [InlineData(7.99, 7)]
    [InlineData(2.0, 3)] // min 3
    [InlineData(0.5, 3)]
    [InlineData(30.0, 30)] // exactly the ceiling of 10 dice × 3 hours
    [InlineData(30.5, 30)] // just over it
    [InlineData(100.0, 30)] // D-96 (2): coins share the dice ceiling
    public void Coins_are_hours_times_rate_floored_but_not_below_the_minimum(double hours, int coins)
    {
        var s = Playing((decimal)hours);
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new CoinsChanged(s.PlayerId("Вася"), coins, CoinsReason.CompletionReward, runId),
            Assert.Single(s.LastEvents<CoinsChanged>()));
        Assert.Equal(coins, s.Player("Вася").Coins);
    }

    [Theory]
    [InlineData(1.5, 0, 5.0, 7)] // 7.5 → 7
    [InlineData(0.5, 0, 3.0, 1)] // 1.5 → 1
    [InlineData(1.0, 10, 8.0, 10)] // 8 → min 10
    [InlineData(2.0, 1, 0.25, 1)] // 0.5 → 0 → min 1
    public void Coins_follow_the_configured_rate_and_minimum(double perHour, int min, double hours, int coins)
    {
        var s = Playing((decimal)hours, Coins((decimal)perHour, min));

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(coins, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Estimate_above_the_ceiling_gives_the_ceiling()
    {
        // D-96 (2): a player's estimate of 1000 hours must not give 1000 coins
        var s = Playing(null);

        s.Complete("Вася", Difficulty.Normal, estimatedHours: 1000, hoursSource: "HLTB");

        ScenarioAssert.Accepted(s);
        Assert.Equal(30, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
        Assert.Equal(30, s.Player("Вася").Coins);
    }

    [Fact]
    public void Pool_hours_above_the_ceiling_give_the_ceiling()
    {
        var s = Playing(1000);

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(30, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Ceiling_is_multiplied_by_the_rate()
    {
        // 100 hours → capped at 30, × 2 per hour
        var s = Playing(100, Coins(2, 3));

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(60, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Theory]
    [InlineData(5.0, 4, 20)] // 4 dice × 5 hours
    [InlineData(2.5, 3, 7)] // 7.5 hours → floor 7
    public void Ceiling_follows_the_dice_count_config(double hoursPerDie, int maxDice, int coins)
    {
        var s = Playing(100, r => r with
        {
            Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = (decimal)hoursPerDie, Max = maxDice } },
        });

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(coins, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Minimum_above_the_ceiling_still_wins()
    {
        // max(min, …): a minimum of 50 is paid even though the ceiling gives 30
        var s = Playing(100, Coins(1, 50));

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(50, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Ceiling_comes_from_the_roll_snapshot()
    {
        // S1: the dice ceiling is fixed at the roll (10 × 3 = 30); the admin lowers diceCount.max to 2 while Вася plays
        var s = Playing(100);
        s.WithRuleset(r => r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { Max = 2 } } });

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(30, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
        Assert.Equal(10, Assert.Single(s.LastEvents<CompletionRolled>()).Dice.Count);
    }

    [Fact]
    public void Zero_rate_and_zero_minimum_give_no_coins_event()
    {
        // Zero changes are not logged (the fold in the invariants refuses them)
        var s = Playing(6, Coins(0, 0));

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Equal(0, s.Player("Вася").Coins);
    }

    [Fact]
    public void Estimate_counts_when_the_pool_had_no_hours()
    {
        var s = Playing(null);

        s.Complete("Вася", Difficulty.Normal, estimatedHours: 10, hoursSource: "HLTB");

        ScenarioAssert.Accepted(s);
        Assert.Equal(10, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Pool_hours_count_rather_than_the_estimate()
    {
        var s = Playing(6);

        s.Complete("Вася", Difficulty.Normal, estimatedHours: 30, hoursSource: "HLTB");

        ScenarioAssert.Accepted(s);
        Assert.Equal(6, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Coins_do_not_depend_on_difficulty_or_the_challenge()
    {
        var s = Playing(6, r => r with { Features = r.Features with { Challenges = true } });

        s.Complete("Вася", Difficulty.Extreme, challengeDone: true);

        ScenarioAssert.Accepted(s);
        Assert.Equal(6, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Coins_come_after_the_points_and_the_move()
    {
        var s = Playing(6);

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        var events = s.Last.Events.ToList();
        var points = events.FindIndex(e => e is PointsChanged);
        var moved = events.FindIndex(e => e is PlayerMoved);
        var coins = events.FindIndex(e => e is CoinsChanged);
        Assert.True(points >= 0 && moved > points && coins > moved, $"Wrong order: {string.Join(", ", events.Select(e => e.GetType().Name))}.");
    }

    [Fact]
    public void Coin_reward_changed_after_the_roll_does_not_change_the_run()
    {
        // S1: the reward is fixed at the roll (1 per hour, min 3); the admin raises it while Вася plays
        var s = Playing(12);
        s.WithRuleset(Coins(10, 50));

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(12, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Coin_reward_changed_before_the_roll_applies_to_that_run()
    {
        var s = Playing(12, Coins(2, 3));

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(24, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
    }

    [Fact]
    public void Coins_add_to_the_balance_across_runs()
    {
        // Given Вася has 2 coins from the admin
        var s = Scenario.New()
            .WithCategory("Short").WithGame("A", 4, "Short").WithGame("B", 12, "Short")
            .WithPlayers("Вася");
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Приз", CoinsDelta: 2));
        ScenarioAssert.Accepted(s);

        s.Roll("Вася").Start("Вася").Complete("Вася");
        s.Roll("Вася").Start("Вася").Complete("Вася");

        // Then 2 + 4 + 12, and every completion coin is logged with its run
        Assert.Equal(18, s.Player("Вася").Coins);
        var rewards = s.Log.OfType<CoinsChanged>().Where(e => e.Reason == CoinsReason.CompletionReward).ToList();
        Assert.Equal(2, rewards.Count);
        Assert.All(rewards, e => Assert.NotNull(e.RunId));
        Assert.Equal(16, rewards.Sum(e => e.Delta));
    }

    [Fact]
    public void Coins_are_given_while_standing_on_the_finish()
    {
        // D-47: no move on the finish, but the reward is still paid
        var s = Scenario.New()
            .WithMapLength(1)
            .WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithGame("Alan Wake", 5, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася").NextRandom(2).Complete("Вася");
        Assert.Equal(LinearMap.FinishId, s.Player("Вася").CellId);

        s.Roll("Вася").Start("Вася").Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Single(s.LastEvents<CoinsChanged>());
        Assert.Equal(3 + 5, s.Player("Вася").Coins);
    }

    [Fact]
    public void Drop_gives_no_coins()
    {
        // SPEC «Экономика»: «За дроп монеток нет»
        var s = Playing(12);

        s.Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Equal(0, s.Player("Вася").Coins);
    }
}
