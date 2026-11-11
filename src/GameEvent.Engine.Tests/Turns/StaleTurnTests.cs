using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Turns;

/// <summary>
/// K1S-11 (D-600): a command sent from a tab that showed an older state says what the player saw — the price of the
/// reroll, the offered game, the active run — and the engine refuses it when that is no longer so.
/// </summary>
public sealed class StaleTurnTests
{
    private static Scenario Horror()
    {
        var s = Scenario.New(seed: 42).WithCategory("Horror");
        foreach (var game in new[] { "Silent Hill", "Alan Wake", "Outlast", "Amnesia", "Soma" })
        {
            s.WithGame(game, 12, "Horror");
        }

        return s.WithPlayers("Вася", "Петя");
    }

    private static Scenario WithCoins(Scenario s, string player, int coins) =>
        s.Act(new AdjustPlayer(s.PlayerId(player), "Стартовый капитал", CoinsDelta: coins));

    [Fact]
    public void A_reroll_that_expects_the_free_one_after_it_was_spent_is_refused_and_takes_no_coins()
    {
        // Given Вася spent his free reroll and has 20 coins
        var s = WithCoins(Horror(), "Вася", 20).Roll("Вася");
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        // When an old tab, still showing «Реролл — бесплатно», sends a reroll that expects a free one
        // Then it is refused and nothing changes
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new Reroll(x.PlayerId("Вася"), ExpectedPayment: RerollPayment.FreeThisRoll)), RejectionCodes.TurnStale);
        Assert.Equal(20, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_reroll_that_expects_the_price_it_costs_is_accepted()
    {
        var s = WithCoins(Horror(), "Вася", 20).Roll("Вася");
        s.Act(new Reroll(s.PlayerId("Вася"), ExpectedPayment: RerollPayment.FreeThisRoll));
        ScenarioAssert.Accepted(s);

        s.Act(new Reroll(s.PlayerId("Вася"), ExpectedPayment: RerollPayment.Coins));

        ScenarioAssert.Accepted(s);
        Assert.Equal(15, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_start_of_a_game_that_is_no_longer_offered_is_refused()
    {
        // Given the tab showed the first offer, then a reroll elsewhere offered another game
        var s = Horror().Roll("Вася");
        var seen = s.Player("Вася").Offer!.GameId;
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        Assert.NotEqual(seen, s.Player("Вася").Offer!.GameId);

        // Then «Начать» from that tab does not start the game the player never saw
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new StartRun(x.PlayerId("Вася"), GameId: seen)), RejectionCodes.TurnStale);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
    }

    [Fact]
    public void A_start_of_the_offered_game_is_accepted()
    {
        var s = Horror().Roll("Вася");
        var offered = s.Player("Вася").Offer!.GameId;

        s.Act(new StartRun(s.PlayerId("Вася"), GameId: offered));

        ScenarioAssert.Accepted(s);
        Assert.Equal(offered, s.State.Runs[s.Player("Вася").ActiveRunId!.Value].GameId);
    }

    [Fact]
    public void A_drop_of_a_run_that_is_no_longer_active_is_refused()
    {
        var s = Horror().Roll("Вася").Start("Вася");
        var active = s.Player("Вася").ActiveRunId!.Value;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new DropRun(x.PlayerId("Вася"), RunId: Guid.NewGuid())), RejectionCodes.TurnStale);

        s.Act(new DropRun(s.PlayerId("Вася"), RunId: active));
        ScenarioAssert.Accepted(s);
    }
}
