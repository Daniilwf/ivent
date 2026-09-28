using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC6: bets (SPEC «Ставки», D-414) — on another player's current run within the window after its roll, a stake in the
/// pledge, a win by the multiplier of hours per day, lost by a drop, a tech reroll, the deadline of the bet or of the
/// season, and taken back by a reject.
/// </summary>
public class BetTests
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

    [Fact]
    public void A_bet_pledges_the_stake_and_pays_by_the_multiplier_when_the_run_is_completed_in_time()
    {
        var s = Season();

        s.Bet("Вася", "Петя", days: 3, stake: 5);

        var bet = Assert.Single(s.LastEvents<BetPlaced>()).Bet;
        Assert.Equal((s.PlayerId("Петя"), 5, 2.0m, s.Clock.UtcNow.AddDays(3)), (bet.OnPlayerId, bet.Stake, bet.Multiplier, bet.Deadline));
        Assert.Equal((-5, CoinsReason.BetStake), (s.LastEvents<CoinsChanged>().Single().Delta, s.LastEvents<CoinsChanged>().Single().Reason));
        Assert.Equal(15, s.Player("Вася").Coins);

        s.Advance(TimeSpan.FromDays(2));
        s.NextRandom(1, 1).Complete("Петя");

        var settled = Assert.Single(s.LastEvents<BetSettled>());
        Assert.Equal((BetStatus.Won, 10), (settled.Status, settled.Payout));
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Вася") && c.Delta == 10 && c.Reason == CoinsReason.BetPayout);
        Assert.Equal(25, s.Player("Вася").Coins);
    }

    [Theory]
    [InlineData(1, "3")] // 6 h in 1 day: 6 h/day → the open step
    [InlineData(3, "2.0")] // 2 h/day
    [InlineData(7, "1.2")] // 0.86 h/day
    public void The_multiplier_follows_the_hours_per_day_the_game_needs(int days, string multiplier)
    {
        var s = Season();

        s.Bet("Вася", "Петя", days, stake: 1);

        Assert.Equal(decimal.Parse(multiplier, System.Globalization.CultureInfo.InvariantCulture), Assert.Single(s.LastEvents<BetPlaced>()).Bet.Multiplier);
    }

    [Fact]
    public void A_game_without_hours_takes_the_first_step()
    {
        var s = Season();
        s.RollTitle("Коля", "Tetris").Start("Коля");

        s.Bet("Вася", "Коля", days: 1, stake: 1);

        Assert.Equal(1.2m, Assert.Single(s.LastEvents<BetPlaced>()).Bet.Multiplier);
    }

    [Fact]
    public void Completing_after_the_bets_deadline_loses_it()
    {
        var s = Season();
        s.Bet("Вася", "Петя", days: 1, stake: 5);

        s.Advance(TimeSpan.FromDays(2));
        s.NextRandom(1, 1).Complete("Петя");

        Assert.Equal(BetStatus.Lost, Assert.Single(s.LastEvents<BetSettled>()).Status);
        Assert.Equal(15, s.Player("Вася").Coins);
    }

    [Fact]
    public void The_deadline_of_the_bet_passing_loses_it_by_the_timer()
    {
        var s = Season();
        s.Bet("Вася", "Петя", days: 1, stake: 5);
        Assert.Equal(s.Clock.UtcNow.AddDays(1), Timers.Next(s.Player("Вася")));

        s.Advance(TimeSpan.FromDays(1));
        s.FireTimers();

        Assert.Equal(BetStatus.Lost, Assert.Single(s.LastEvents<BetSettled>()).Status);
        Assert.Null(Timers.Next(s.Player("Вася")));
    }

    [Fact]
    public void A_drop_or_a_tech_reroll_loses_the_bets_on_the_run()
    {
        var s = Season();
        s.Bet("Вася", "Петя", days: 3, stake: 5);
        s.Bet("Коля", "Петя", days: 7, stake: 2);

        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Петя")));

        Assert.Equal([BetStatus.Lost, BetStatus.Lost], s.LastEvents<BetSettled>().Select(b => b.Status));

        s.RollTitle("Петя", "Gothic").Start("Петя");
        s.Bet("Вася", "Петя", days: 3, stake: 5);
        s.NextRandom(0, 0).Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.WeakPc, null));
        Assert.Equal(BetStatus.Lost, Assert.Single(s.LastEvents<BetSettled>()).Status);
    }

    [Fact]
    public void A_reject_after_the_payout_takes_it_back()
    {
        var s = Season();
        s.Bet("Вася", "Петя", days: 3, stake: 5);
        s.NextRandom(1, 1).Complete("Петя");
        var run = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Петя"));

        s.Act(new RejectProof(run.RunId, "Нет титров"));

        var revoked = Assert.Single(s.LastEvents<BetSettled>());
        Assert.Equal((BetStatus.Revoked, 10), (revoked.Status, revoked.Payout));
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Вася") && c.Delta == -10 && c.Reason == CoinsReason.BetPayoutRevoked);
        Assert.Equal(15, s.Player("Вася").Coins);
    }

    [Fact]
    public void The_season_closing_loses_the_open_bets()
    {
        var s = Season();
        s.Bet("Вася", "Петя", days: 7, stake: 5);

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        Assert.Equal(BetStatus.Lost, Assert.Single(s.LastEvents<BetSettled>()).Status);
    }

    [Theory]
    [InlineData("self", RejectionCodes.BetOnSelf)]
    [InlineData("noRun", RejectionCodes.BetNoRun)]
    [InlineData("late", RejectionCodes.BetWindowClosed)]
    [InlineData("days", RejectionCodes.BetInvalidDays)]
    [InlineData("zero", RejectionCodes.BetInvalidStake)]
    [InlineData("big", RejectionCodes.BetInvalidStake)]
    [InlineData("twice", RejectionCodes.BetAlreadyPlaced)]
    [InlineData("poor", RejectionCodes.NotEnoughCoins)]
    [InlineData("debt", RejectionCodes.CoinsInDebt)]
    [InlineData("unknown", RejectionCodes.PlayerUnknown)]
    [InlineData("off", RejectionCodes.FeatureDisabled)]
    public void A_bet_is_refused_when_the_rules_do_not_allow_it(string what, string code)
    {
        var s = Season();
        var (player, on, days, stake) = ("Вася", "Петя", 3, 5);
        switch (what)
        {
            case "self": on = "Вася"; break;
            case "noRun": on = "Коля"; break;
            case "late": s.Advance(TimeSpan.FromHours(25)); break;
            case "days": days = 2; break;
            case "zero": stake = 0; break;
            case "big": stake = 11; break;
            case "twice": s.Bet("Вася", "Петя", 3, 1); break;
            case "poor": s.WithCoins("Вася", 4); break;
            case "debt": s.WithCoins("Вася", -1); stake = 1; break;
            case "off": s.WithRuleset(r => r with { Features = r.Features with { Bets = false } }); break;
            default: break;
        }

        if (what == "unknown")
        {
            s.Act(new PlaceBet(s.PlayerId(player), Guid.NewGuid(), days, stake));
        }
        else
        {
            s.Bet(player, on, days, stake);
        }

        Assert.Equal(code, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Open_bets_are_limited()
    {
        var s = Season();
        s.WithRuleset(r => r with { Bets = r.Bets with { MaxOpenBetsPerPlayer = 1 } });
        s.RollTitle("Коля", "Gothic").Start("Коля");
        s.Bet("Вася", "Петя", 3, 1);

        s.Bet("Вася", "Коля", 3, 1);

        Assert.Equal(RejectionCodes.BetTooMany, s.Last.Rejection!.Code);
    }

    [Fact]
    public void No_bet_on_the_first_finisher()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Gothic", 30, "Horror").WithMapLength(2).WithPlayers("Вася", "Петя");
        s.WithCoins("Вася", 20);
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        s.RollTitle("Петя", "Gothic").Start("Петя");

        s.Bet("Вася", "Петя", 3, 1);

        Assert.Equal(RejectionCodes.BetOnFirst, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_frozen_first_wins_a_bet_without_coins()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Gothic", 30, "Horror")
            .WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }).WithMapLength(2).WithPlayers("Вася", "Петя");
        s.WithCoins("Вася", 20);
        s.RollTitle("Петя", "Gothic").Start("Петя");
        s.Bet("Вася", "Петя", 7, 5);
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");
        Assert.True(s.Player("Вася").Finish!.Frozen);

        s.NextRandom([.. Enumerable.Repeat(1, 10)]).Complete("Петя");

        var won = Assert.Single(s.LastEvents<BetSettled>());
        Assert.Equal((BetStatus.Won, 0), (won.Status, won.Payout));
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Вася"));
    }
}
