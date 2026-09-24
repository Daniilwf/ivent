using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// G10: when no game is left for a player, the admin gets a signal (SPEC «Уточнения»: пустой пул; D-92). The signal is
/// computed, not logged: <see cref="PoolStats.PlayersWithoutGames"/> lists players of a running season whose next roll
/// would find nothing, their own exclusions included, ordered by name. Dropping the zone filter is stage 2.
/// A rolling or playing player whose only game is his own offer is not asserted on (SPEC does not say), the tests
/// judge idle players and players who still have a free game.
/// </summary>
public class EmptyPoolTests
{
    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 40)];

    private static IReadOnlyList<Guid> Without(Scenario s) => PoolStats.PlayersWithoutGames(s.State, s.Context().Pool);

    private static void Exclude(Scenario s, string player)
    {
        s.Act(new DeclareAlreadyPlayed(s.PlayerId(player), s.Player(player).Offer!.GameId));
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Nobody_is_listed_while_everyone_has_a_game()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
            .WithPlayers("Вася", "Петя");

        Assert.Empty(Without(s));
    }

    [Fact]
    public void Player_who_excluded_every_game_is_listed_and_others_are_not()
    {
        // Given Вася excluded both games of the pool, one after another
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася", "Петя")
            .Roll("Вася");
        Exclude(s, "Вася");
        s.WithGame("Alan Wake", 15, "Horror").Roll("Вася");
        Exclude(s, "Вася");
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);

        // Then he is the signal; Петя can still get both games
        Assert.Equal([s.PlayerId("Вася")], Without(s));
    }

    [Fact]
    public void Exclusions_and_busy_games_together_leave_a_player_without_games()
    {
        var sawSignal = false;
        foreach (var seed in s_seeds)
        {
            // Given Вася excluded Silent Hill, then Alan Wake joins the pool and Петя rolls one of the two
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
                .WithPlayers("Вася", "Петя")
                .Roll("Вася");
            Exclude(s, "Вася");
            s.WithGame("Alan Wake", 15, "Horror").Roll("Петя");
            var petyaGame = s.Player("Петя").Offer!.GameId;

            if (petyaGame == s.GameId("Alan Wake"))
            {
                // Then Вася has his excluded game and Петя's busy one: nothing; Петя still has Silent Hill
                Assert.Equal([s.PlayerId("Вася")], Without(s));
                sawSignal = true;
            }
            else
            {
                // Петя took Silent Hill: Alan Wake is free for Вася
                Assert.Empty(Without(s));
            }
        }

        Assert.True(sawSignal, "Over many seeds Петя must sometimes take the last game Вася could get.");
    }

    [Fact]
    public void Everyone_is_listed_by_name_when_the_pool_is_empty()
    {
        // Players are added in an order different from their names
        var s = Scenario.New().WithCategory("Horror").WithDeletedGame("Gone", 5, "Horror").WithPlayers("Петя", "Вася", "Аня");

        Assert.Equal([s.PlayerId("Аня"), s.PlayerId("Вася"), s.PlayerId("Петя")], Without(s));
    }

    [Fact]
    public void Everyone_is_listed_when_the_last_game_is_completed()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася", "Петя")
            .Roll("Вася").Start("Вася").Complete("Вася");

        Assert.Equal([s.PlayerId("Вася"), s.PlayerId("Петя")], Without(s));
    }

    [Fact]
    public void Signal_goes_away_when_a_game_is_freed()
    {
        var checkedSeeds = 0;
        foreach (var seed in s_seeds)
        {
            // Given Вася excluded Silent Hill and Петя holds Alan Wake, the only other game
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
                .WithPlayers("Вася", "Петя")
                .Roll("Вася");
            Exclude(s, "Вася");
            s.WithGame("Alan Wake", 15, "Horror").Roll("Петя");
            if (s.Player("Петя").Offer!.GameId != s.GameId("Alan Wake"))
            {
                continue;
            }

            checkedSeeds++;
            Assert.Equal([s.PlayerId("Вася")], Without(s));

            // When the admin discards Петя's offer, Alan Wake is free again and the signal is gone
            s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Сброс", DiscardOffer: true));
            ScenarioAssert.Accepted(s);

            Assert.Empty(Without(s));
        }

        Assert.True(checkedSeeds > 0, "Over many seeds Петя must sometimes roll Alan Wake.");
    }

    [Theory]
    [InlineData(SeasonStatus.Closing)]
    [InlineData(SeasonStatus.Finished)]
    public void Nobody_is_listed_when_the_season_is_not_running(SeasonStatus status)
    {
        var s = Scenario.New().WithCategory("Horror").WithPlayers("Вася", "Петя");
        Assert.Equal(2, Without(s).Count);

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        if (status == SeasonStatus.Finished)
        {
            s.Act(new ChangeSeasonStatus(SeasonStatus.Finished));
        }

        ScenarioAssert.Accepted(s);
        Assert.Empty(Without(s));
    }

    [Fact]
    public void Nobody_is_listed_in_a_draft_season()
    {
        var s = Scenario.New().AsDraft().WithCategory("Horror").WithPlayers("Вася");

        Assert.Empty(Without(s));
    }
}
