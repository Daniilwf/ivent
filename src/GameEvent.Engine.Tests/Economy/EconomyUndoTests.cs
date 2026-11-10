using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC7: undo of economy commands (D-104, D-401) — the economy lives in the player, so a compensation restores it with the
/// player; a later command touching the same player makes the undo wait for it.
/// </summary>
public class EconomyUndoTests
{
    private static Scenario Season()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
        return s.WithContent(EconomyScenario.RepositoryPack());
    }

    private static void AssertUndoneToTheLogWithout(Scenario s)
    {
        Assert.True(s.Last.IsAccepted, s.Last.Rejection?.ToString());
        Assert.Equal(SeasonEngine.Replay(s.EffectiveLog).Players, s.State.Players);
    }

    [Fact]
    public void Undoing_an_item_used_on_another_player_gives_back_the_item_and_the_coins()
    {
        var s = Season().WithCoins("Петя", 5);
        s.Give("Вася", "bird-thief");
        s.NextRandom(3).Used("Вася", "bird-thief", target: "Петя");
        var used = s.LastCommandId;

        s.Act(new UndoCommand(used, "Ошибся целью"));

        AssertUndoneToTheLogWithout(s);
        Assert.Equal(["bird-thief"], s.Inventory("Вася"));
        Assert.Equal((5, 0, 0), (s.Player("Петя").Coins, s.Player("Вася").Coins, s.Player("Петя").Wallet.HostileReceived));
    }

    [Fact]
    public void Undoing_a_purchase_gives_back_the_coins_and_the_lot()
    {
        var s = Season().WithCoins("Вася", 30);
        s.RollShop("Вася");
        s.Buy("Вася", 0);
        var bought = s.LastCommandId;

        s.Act(new UndoCommand(bought, "Не то купил"));

        AssertUndoneToTheLogWithout(s);
        Assert.Empty(s.Inventory("Вася"));
        Assert.False(s.Player("Вася").Wallet.Shop!.Lots[0].Sold);
        Assert.Equal(25, s.Player("Вася").Coins);
    }

    [Fact]
    public void Undoing_the_first_gift_leaves_the_player_without_an_economy()
    {
        var s = Season();
        s.Give("Вася", "orange");

        s.Act(new UndoCommand(s.LastCommandId, "Лишний"));

        AssertUndoneToTheLogWithout(s);
        Assert.Null(s.Player("Вася").Economy);
    }

    [Fact]
    public void A_bet_settled_by_a_later_completion_waits_for_it()
    {
        var s = Season().WithCoins("Вася", 10);
        s.RollTitle("Петя", "Silent Hill").Start("Петя");
        s.Bet("Вася", "Петя", 3, 5);
        var placed = s.LastCommandId;
        s.NextRandom(1, 1).Complete("Петя");
        var completed = s.LastCommandId;

        s.Act(new UndoCommand(placed, "Ставку отменить"));
        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Contains(completed, s.Last.Rejection.Related);

        s.Act(new UndoCommand(completed, "Завершение по ошибке"));
        AssertUndoneToTheLogWithout(s);
        Assert.Equal(BetStatus.Open, Assert.Single(s.Player("Вася").Wallet.Bets).Status);

        s.Act(new UndoCommand(placed, "Ставку отменить"));
        AssertUndoneToTheLogWithout(s);
        Assert.Empty(s.Player("Вася").Wallet.Bets);
        Assert.Equal(10, s.Player("Вася").Coins);
    }

    [Fact]
    public void Undoing_a_completion_brings_back_the_effect_it_spent()
    {
        var s = Season();
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1, 6).Complete("Петя");
        Assert.Empty(s.Inventory("Петя"));

        s.Act(new UndoCommand(s.LastCommandId, "Пересчитать"));

        AssertUndoneToTheLogWithout(s);
        Assert.Equal(["curse-effect"], s.Inventory("Петя"));
    }
}
