using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// K1A: the admin deletes from the pool a game that is a player's offer (a duplicate, say). It is no longer started:
/// the player rerolls it for free, and the free rerolls of the roll stay (D-615). A run already under way keeps its
/// game and its snapshot (SPEC «Мягкое удаление»).
/// </summary>
public class DeletedOfferTests
{
    private static Scenario Offered()
    {
        var s = Scenario.New().WithCategory("Horror")
            .WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 12, "Horror").WithGame("Outlast", 12, "Horror")
            .WithPlayers("Вася");
        return s.RollTitle("Вася", "Silent Hill");
    }

    [Fact]
    public void An_offered_game_deleted_from_the_pool_is_not_started()
    {
        var s = Offered().DeleteGame("Silent Hill");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Start("Вася"), RejectionCodes.GameDeleted);
    }

    [Fact]
    public void Its_reroll_is_free_and_keeps_the_free_rerolls_of_the_roll()
    {
        var s = Offered().DeleteGame("Silent Hill");
        var coins = s.Player("Вася").Coins;

        s.Act(new Reroll(s.PlayerId("Вася")));

        var rerolled = Assert.Single(s.LastEvents<GameRerolled>());
        Assert.Equal(RerollPayment.GameDeleted, rerolled.Payment);
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Rolling, player.Phase);
        Assert.NotEqual(s.GameId("Silent Hill"), player.Offer!.GameId);
        Assert.Equal(0, player.RerollsThisRoll);
        Assert.Equal(coins, player.Coins);
        Assert.Equal((RerollPayment.FreeThisRoll, 0), RerollPrice.For(player, s.State.Rules.Roll));
    }

    [Fact]
    public void A_game_still_in_the_pool_is_rerolled_as_before()
    {
        var s = Offered();

        s.Act(new Reroll(s.PlayerId("Вася")));

        Assert.Equal(RerollPayment.FreeThisRoll, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal(1, s.Player("Вася").RerollsThisRoll);
    }

    [Fact]
    public void A_run_under_way_keeps_its_deleted_game()
    {
        var s = Offered().Start("Вася").DeleteGame("Silent Hill");

        s.Complete("Вася");

        Assert.True(s.Last.IsAccepted);
    }
}
