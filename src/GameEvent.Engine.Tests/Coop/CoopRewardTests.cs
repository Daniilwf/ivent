using FsCheck.Xunit;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// The reward of a coop run (EV26, EV41, EV46; D-521, D-540): one throw, split — the roller takes ⌈total ×
/// <c>reward.coop.pointsShare</c>⌉ points and steps, the partner the rest, each moves his own share by his own move; the
/// completion coins are split the same way by <c>reward.coop.coinsShare</c>. The split is logged as
/// <see cref="CoopRewardSplit"/> with the shares of the moment, and every later correction re-splits the new total by them.
/// The partner's points and coins come from the system (<c>CompletionReward</c>), never from the roller. Scenarios
/// S5.10-10, -11, -12, -22 of the stage 5 plan.
/// </summary>
public class CoopRewardTests
{
    [Fact]
    public void Points_and_steps_are_split_with_the_rounding_up_for_the_roller()
    {
        var s = New();
        var run = s.CoopRun();
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new CoopRewardSplit(run, vasya, petya, Total: 7, RollerPoints: 4, PartnerPoints: 3, Coins: 6, RollerCoins: 3, PartnerCoins: 3, PointsShare: 0.5m, CoinsShare: 0.5m),
            Assert.Single(s.LastEvents<CoopRewardSplit>()));
        Assert.Contains(new PointsChanged(vasya, 4, PointsReason.CompletionRoll, run), s.Last.Events);
        Assert.Contains(new PlayerMoved(vasya, LinearMap.StartId, "c4", 4, ["c1", "c2", "c3", "c4"], MoveReason.CompletionRoll, run), s.Last.Events);
        Assert.Contains(new CoinsChanged(vasya, 3, CoinsReason.CompletionReward, run), s.Last.Events);
        Assert.Contains(new PointsChanged(petya, 3, PointsReason.CompletionRoll, run), s.Last.Events);
        Assert.Contains(new PlayerMoved(petya, LinearMap.StartId, "c3", 3, ["c1", "c2", "c3"], MoveReason.CompletionRoll, run), s.Last.Events);
        Assert.Contains(new CoinsChanged(petya, 3, CoinsReason.CompletionReward, run), s.Last.Events);

        Assert.Equal((4, 3, "c4"), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
        Assert.Equal((3, 3, "c3"), (s.Player("Петя").Points, s.Player("Петя").Coins, s.Player("Петя").CellId));

        // The shares of the moment stay with the run (D-521): later corrections split by them
        Assert.NotNull(s.State.Runs[run].Coop!.Shares);
    }

    [Fact]
    public void The_completion_logs_the_split_then_the_rollers_part_then_the_partners()
    {
        var s = New();
        s.CoopRun();
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunCompleted), typeof(CompletionRolled), typeof(CoopRewardSplit),
             typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged),
             typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(
            [vasya, vasya, vasya, petya, petya, petya],
            s.Last.Events.Skip(3).Select(e => e switch
            {
                PointsChanged p => p.PlayerId,
                PlayerMoved m => m.PlayerId,
                CoinsChanged c => c.PlayerId,
                _ => Guid.Empty,
            }));
    }

    [Theory]
    [InlineData(0.5, 7, 4, 3)]
    [InlineData(0.5, 2, 1, 1)]
    [InlineData(0.75, 7, 6, 1)]
    [InlineData(1.0, 7, 7, 0)]
    [InlineData(0.1, 7, 1, 6)]
    public void The_points_share_comes_from_the_ruleset(double share, int total, int roller, int partner)
    {
        var s = New(r => r.WithCoop(pointsShare: (decimal)share));
        s.CoopRun();
        int[] dice = total == 2 ? [1, 1] : [3, 4];

        s.NextRandom(dice).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var split = Assert.Single(s.LastEvents<CoopRewardSplit>());
        Assert.Equal((total, roller, partner, (decimal)share), (split.Total, split.RollerPoints, split.PartnerPoints, split.PointsShare));
        Assert.Equal((roller, partner), (s.Player("Вася").Points, s.Player("Петя").Points));
    }

    [Fact]
    public void A_zero_share_moves_the_partner_nowhere()
    {
        // pointsShare 1: the partner's share is 0 — no move of his, no points change of his
        var s = New(r => r.WithCoop(pointsShare: 1m));
        s.CoopRun();

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(s.LastEvents<PlayerMoved>(), m => m.PlayerId == s.PlayerId("Петя"));
        Assert.Equal((0, LinearMap.StartId), (s.Player("Петя").Points, s.Player("Петя").CellId));
    }

    // ---- Coins (S5.10-11) ----

    [Theory]
    [InlineData(7.0, 1.0, new[] { 1, 1 }, 7, 4, 3)] // 7 hours: two d4
    [InlineData(1.0, 1.0, new[] { 1 }, 1, 1, 0)] // 1 hour: one d4 (the dice minimum)
    [InlineData(7.0, 0.0, new[] { 1, 1 }, 0, 0, 0)]
    public void Coins_are_split_like_points_with_the_rounding_up_for_the_roller(double hours, double perHour, int[] dice, int coins, int roller, int partner)
    {
        // «Portal 2» of the given hours, coins = hours × perHour without a minimum
        var s = New(
            r => r with { Reward = r.Reward with { Coins = new CoinReward { PerHour = (decimal)perHour, Min = 0 } } },
            coopHours: (decimal)hours);
        var run = s.CoopRun();

        s.NextRandom(dice).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var split = Assert.Single(s.LastEvents<CoopRewardSplit>());
        Assert.Equal((run, coins, roller, partner, 0.5m), (split.RunId, split.Coins, split.RollerCoins, split.PartnerCoins, split.CoinsShare));
        Assert.Equal((roller, partner), (s.Player("Вася").Coins, s.Player("Петя").Coins));
    }

    [Theory]
    [InlineData(0.5, 3, 3)]
    [InlineData(0.75, 5, 1)]
    [InlineData(1.0, 6, 0)]
    public void The_coins_share_comes_from_the_ruleset(double share, int roller, int partner)
    {
        var s = New(r => r.WithCoop(coinsShare: (decimal)share));
        s.CoopRun();

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var split = Assert.Single(s.LastEvents<CoopRewardSplit>());
        Assert.Equal((CoinsPerRun, roller, partner), (split.Coins, split.RollerCoins, split.PartnerCoins));
        Assert.Equal((roller, partner), (s.Player("Вася").Coins, s.Player("Петя").Coins));
    }

    // ---- The sum of the shares (S5.10-12, EV46) ----

    /// <summary>EV46: for any total 0…60 and share in (0, 1] the shares are whole, not negative and add up to the total.</summary>
    [Property(MaxTest = 500)]
    public void Shares_add_up_to_the_throw(byte totalSeed, byte shareSeed)
    {
        var total = totalSeed % 61;
        var share = ((shareSeed % 100) + 1) / 100m;

        var (roller, partner) = CoopShares.Split(total, share);

        Assert.Equal((int)Math.Ceiling(total * share), roller);
        Assert.Equal(total - roller, partner);
        Assert.True(roller >= 0 && partner >= 0, $"total {total}, share {share}: {roller}/{partner}");
        Assert.Equal(total, roller + partner);
        if (share >= 0.5m)
        {
            Assert.True(roller >= partner, $"total {total}, share {share}: the roller {roller} below the partner {partner}");
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(7, 4, 3)]
    [InlineData(8, 4, 4)]
    [InlineData(60, 30, 30)]
    public void Half_shares_round_up_for_the_roller(int total, int roller, int partner)
    {
        Assert.Equal((roller, partner), CoopShares.Split(total, 0.5m));
    }

    [Property(MaxTest = 30)]
    public void The_split_of_any_throw_adds_up_to_it(byte first, byte second)
    {
        var dice = new[] { (first % 4) + 1, (second % 4) + 1 };
        var s = New();
        var run = s.CoopRun();

        s.NextRandom(dice).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var split = Assert.Single(s.LastEvents<CoopRewardSplit>());
        Assert.Equal(dice.Sum(), split.Total);
        Assert.Equal(split.Total, split.RollerPoints + split.PartnerPoints);
        Assert.Equal(split.Coins, split.RollerCoins + split.PartnerCoins);
        Assert.Equal(split.Total, s.Player("Вася").Points + s.Player("Петя").Points);
        Assert.Equal(CoinsPerRun, s.Player("Вася").Coins + s.Player("Петя").Coins);
        Assert.Equal(split.Total, s.State.Runs[run].Moved + s.State.Runs[run].Coop!.Moved);
    }

    // ---- No transfers (S5.10-22, EV41) ----

    [Fact]
    public void Coins_come_from_the_system_not_from_the_partner()
    {
        var s = New();
        var run = s.CoopRun();
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));

        s.NextRandom(3, 4).Complete("Вася");
        s.NextRandom(2).Act(new CorrectRunHours(run, 9, "По HLTB 9 часов"));
        ScenarioAssert.Accepted(s);

        // Nothing of either goes down in the completion and the correction: no transfer between them
        var coins = s.Log.OfType<CoinsChanged>().Where(c => c.PlayerId == vasya || c.PlayerId == petya).ToList();
        Assert.All(coins, c => Assert.True(c.Delta >= 0, $"{c} takes coins away"));
        Assert.All(s.Log.OfType<PointsChanged>().Where(p => p.PlayerId == vasya || p.PlayerId == petya), p => Assert.True(p.Delta >= 0, $"{p} takes points away"));

        // The partner's coins are the system's reward for the run (and its correction), his balance is exactly their sum
        Assert.Contains(new CoinsChanged(petya, 3, CoinsReason.CompletionReward, run), coins);
        Assert.All(
            coins.Where(c => c.PlayerId == petya),
            c => Assert.True(c.Reason is CoinsReason.CompletionReward or CoinsReason.RunCorrection && c.RunId == run, $"{c} is not the system's reward for the run"));
        Assert.Equal(coins.Where(c => c.PlayerId == petya).Sum(c => c.Delta), s.Player("Петя").Coins);
        Assert.Equal(coins.Where(c => c.PlayerId == vasya).Sum(c => c.Delta), s.Player("Вася").Coins);

        // One reward for one run: the two shares are the coins of a solo run of 9 hours, not twice that
        Assert.Equal(9, s.Player("Вася").Coins + s.Player("Петя").Coins);
    }

    // ---- Corrections re-split by the shares of the completion (D-521) ----

    [Fact]
    public void An_hours_correction_resplits_the_new_total_by_the_shares_written_at_the_completion()
    {
        // 6 h → two d4 3 + 4 = 7: 4/3, coins 6: 3/3. The admin then sets the shares to 0.75 for the season, and corrects
        // the hours to 9 → one more d4 (2): 9 by the old shares — 5/4, coins 9 — 5/4
        var s = New();
        var run = s.CoopRun();
        s.CompleteWith("Вася", 3, 4);
        s.WithRuleset(r => r.WithCoop(coinsShare: 0.75m, pointsShare: 0.75m));

        s.NextRandom(2).Act(new CorrectRunHours(run, 9, "По HLTB 9 часов"));

        ScenarioAssert.Accepted(s);
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));
        Assert.Contains(new PointsChanged(vasya, 1, PointsReason.RunCorrection, run), s.Last.Events);
        Assert.Contains(new PointsChanged(petya, 1, PointsReason.RunCorrection, run), s.Last.Events);
        Assert.Equal((5, 5, "c5"), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
        Assert.Equal((4, 4, "c4"), (s.Player("Петя").Points, s.Player("Петя").Coins, s.Player("Петя").CellId));
    }

    [Fact]
    public void A_difficulty_change_resplits_the_new_total()
    {
        // Normal 3 + 4 = 7 (4/3) → easy: each d4 becomes ⌈v × 2 / 4⌉ — 2 + 2 = 4 (2/2); coins do not depend on difficulty
        var s = New();
        var run = s.CoopRun();
        s.CompleteWith("Вася", 3, 4);

        s.Act(new ChangeRunDifficulty(run, Difficulty.Easy, "По пруфу — лёгкая"));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new PointsChanged(s.PlayerId("Вася"), -2, PointsReason.RunCorrection, run), s.Last.Events);
        Assert.Contains(new PointsChanged(s.PlayerId("Петя"), -1, PointsReason.RunCorrection, run), s.Last.Events);
        Assert.Equal((2, "c2"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Equal((2, "c2"), (s.Player("Петя").Points, s.Player("Петя").CellId));
        Assert.Equal((3, 3), (s.Player("Вася").Coins, s.Player("Петя").Coins));
    }
}
