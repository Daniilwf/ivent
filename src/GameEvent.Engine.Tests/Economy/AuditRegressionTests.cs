using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// Findings of the rules audit after the economy moved onto the engine rules (D-422): a genre imposed by a special roll
/// or an item is imposed for the wish reroll too (D-325); the frozen first's coins and inventory do not change (Q-3,
/// D-407); a later finisher does not spend an item whose only effect is to move himself (SPEC «Исключения по умолчанию»).
/// </summary>
public class AuditRegressionTests
{
    [Fact]
    public void A_genre_forced_by_an_item_is_imposed_and_refuses_the_wish_reroll()
    {
        var s = EconomyScenario.New().WithRuleset(r => r with { Roll = r.Roll with { WishRerollTags = ["RPG"] } })
            .WithCategory("Horror").WithCategory("RPG").WithGame("Persona", 3, "RPG").WithGame("Silent Hill", 3, "Horror")
            .WithPlayers("Вася", "Петя").WithPoints("Петя", 10);
        s.WithContent(EconomyScenario.RepositoryPack());
        s.Give("Вася", "dirty-trick");
        s.Used("Вася", "dirty-trick", "Петя", "RPG");
        s.Roll("Петя").Start("Петя");

        var run = s.State.Runs[s.Player("Петя").ActiveRunId!.Value];
        Assert.Equal(["RPG"], (IReadOnlyList<string>)run.Snapshot.ImposedTags);
        s.Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.Wish, null, false));

        Assert.Equal(RejectionCodes.WishRerollImposed, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_reject_does_not_take_a_bet_win_back_from_the_frozen_first()
    {
        // Given Вася won a bet on Петя's run, then finished first and is frozen
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Gothic", 30, "Horror")
            .WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }).WithMapLength(5).WithPlayers("Вася", "Петя");
        s.WithCoins("Вася", 20);
        s.RollTitle("Петя", "Silent Hill").Start("Петя");
        s.Bet("Вася", "Петя", 3, 5);
        s.NextRandom(1, 1).Complete("Петя");
        Assert.Equal(BetStatus.Won, Assert.Single(s.Player("Вася").Wallet.Bets).Status);
        var petyasRun = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Петя")).RunId;
        s.RollTitle("Вася", "Gothic").Start("Вася").NextRandom([.. Enumerable.Repeat(1, 10)]).Complete("Вася");
        Assert.True(s.Player("Вася").Finish!.Frozen);
        var coins = s.Player("Вася").Coins;

        // When Петя's run is rejected
        s.Act(new RejectProof(petyasRun, "Нет титров"));

        // Then the bet is revoked, but the frozen first's coins stay
        ScenarioAssert.Accepted(s);
        Assert.Equal(BetStatus.Revoked, Assert.Single(s.LastEvents<BetSettled>()).Status);
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Вася"));
        Assert.Equal(coins, s.Player("Вася").Coins);
    }

    [Fact]
    public void The_frozen_firsts_objects_do_not_expire()
    {
        // Given Вася holds a talisman's effect (3 runs) and finishes first with approval off: frozen
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Gothic", 30, "Horror").WithGame("Risen", 12, "Horror")
            .WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }).WithMapLength(2).WithPlayers("Вася", "Петя");
        s.WithContent(EconomyScenario.RepositoryPack());
        s.Give("Вася", "talisman");
        s.Used("Вася", "talisman");
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");
        Assert.True(s.Player("Вася").Finish!.Frozen);
        var held = s.Player("Вася").Wallet;

        // When he plays on in the free mode
        s.RollTitle("Вася", "Gothic").Start("Вася").NextRandom([.. Enumerable.Repeat(1, 10)]).Complete("Вася");

        // Then his inventory does not change
        Assert.Equal(held.Inventory, s.Player("Вася").Wallet.Inventory);
    }

    [Fact]
    public void A_later_finisher_does_not_spend_an_item_that_only_moves_himself()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror")
            .WithMapLength(2).WithPlayers("Вася", "Петя");
        s.WithContent(EconomyScenario.RepositoryPack());
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        s.RollTitle("Вася", "Dead Space").Start("Вася").NextRandom(1, 1, 1).Complete("Вася");
        Assert.Equal(2, s.Player("Вася").Finish!.Order);
        s.Give("Вася", "banana");

        s.Use("Вася", s.Held("Вася", "banana").InstanceId);

        Assert.Equal(RejectionCodes.ItemNoTarget, s.Last.Rejection!.Code);
        Assert.Equal(["banana"], s.Inventory("Вася"));
    }
}
