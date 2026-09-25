using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// Roll: the category wheel spins only over categories with available games, then the server
/// draws a game; unavailable games hit on the way are logged as misses (SPEC «Пул игр и ролл», D-04, D-05).
/// Randomness is not scripted here: the outcome must hold for every seed, so tests run many seeds.
/// </summary>
public class RollTests
{
    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 40)];

    // ---- Positive: the roll offers a game ----

    [Fact]
    public void Roll_offers_available_game_and_moves_player_to_rolling()
    {
        // Given a pool with one horror game and an idle player
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася")
            .Advance(TimeSpan.FromHours(1));
        var rolledAt = s.Clock.UtcNow;

        // When
        s.Roll("Вася");

        // Then one GameRolled with the game, its category, no misses and the roll-time snapshot
        ScenarioAssert.Accepted(s);
        var expectedSnapshot = new RunSnapshot(
            s.Ruleset.Version, 12m, s.Ruleset.Reward.DiceCount, s.Ruleset.Reward.DieByDifficulty, s.Ruleset.Roll.TechRerollWindowHours,
            s.Ruleset.Reward.ChallengeBonus.ExtraDice, s.Ruleset.Reward.Coins);
        var rolled = Assert.IsType<GameRolled>(Assert.Single(s.Last.Events));
        Assert.Equal(
            new GameRolled(s.PlayerId("Вася"), "Horror", [], s.GameId("Silent Hill"), expectedSnapshot, rolledAt),
            rolled);

        // And the player is Rolling with the game offered (reserved), nothing else changed
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Rolling, player.Phase);
        Assert.Equal(new RollOffer(s.GameId("Silent Hill"), expectedSnapshot, rolledAt), player.Offer);
        Assert.Null(player.ActiveRunId);
        Assert.Equal(0, player.Points);
        Assert.Equal("start", player.CellId);
        Assert.Empty(s.State.Runs);
    }

    [Fact]
    public void Roll_snapshot_keeps_missing_hours_as_null()
    {
        var s = Scenario.New()
            .WithCategory("Indie").WithGame("Unknown Length", null, "Indie")
            .WithPlayers("Вася");

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Null(Assert.Single(s.LastEvents<GameRolled>()).Snapshot.Hours);
        Assert.Null(s.Player("Вася").Offer!.Snapshot.Hours);
    }

    // ---- Category wheel ----

    [Fact]
    public void Wheel_spins_only_over_categories_with_available_games()
    {
        foreach (var seed in s_seeds)
        {
            // Given heavy categories without available games and a light one with a game
            var s = Scenario.New(seed: seed)
                .WithCategory("Empty", weight: 1000)
                .WithCategory("OnlyDeleted", weight: 1000).WithDeletedGame("Gone", 5, "OnlyDeleted")
                .WithCategory("Horror", weight: 1).WithGame("Silent Hill", 12, "Horror")
                .WithPlayers("Вася");

            s.Roll("Вася");

            ScenarioAssert.Accepted(s);
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal("Horror", rolled.Category);
            Assert.Equal(s.GameId("Silent Hill"), rolled.GameId);
            Assert.Empty(rolled.Misses);
        }
    }

    [Fact]
    public void Wheel_skips_category_whose_only_game_is_reserved_by_another_player()
    {
        foreach (var seed in s_seeds)
        {
            // Given Петя holds the only horror game, then a puzzle game appears in the pool
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror", weight: 1000).WithGame("Silent Hill", 12, "Horror")
                .WithPlayers("Вася", "Петя")
                .Roll("Петя");
            ScenarioAssert.Accepted(s);
            s.WithCategory("Puzzle", weight: 1).WithGame("Tetris", 2, "Puzzle");

            // When Вася rolls, the horror category is not on the wheel at all: no miss is even recorded
            s.Roll("Вася");

            ScenarioAssert.Accepted(s);
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal("Puzzle", rolled.Category);
            Assert.Equal(s.GameId("Tetris"), rolled.GameId);
            Assert.Empty(rolled.Misses);
        }
    }

    [Fact]
    public void Wheel_follows_category_weights()
    {
        // Given weights 3 : 1, over many seeds the heavy category wins about 75% of rolls
        const int Rolls = 400;
        var heavy = 0;
        for (var seed = 1; seed <= Rolls; seed++)
        {
            var s = Scenario.New(seed: seed)
                .WithCategory("Heavy", weight: 3).WithGame("Heavy Game", 5, "Heavy")
                .WithCategory("Light", weight: 1).WithGame("Light Game", 5, "Light")
                .WithPlayers("Вася")
                .Roll("Вася");
            ScenarioAssert.Accepted(s);

            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal(rolled.Category == "Heavy" ? s.GameId("Heavy Game") : s.GameId("Light Game"), rolled.GameId);
            if (rolled.Category == "Heavy")
            {
                heavy++;
            }
        }

        var share = (double)heavy / Rolls;
        Assert.InRange(share, 0.65, 0.85);
    }

    // ---- Soft-deleted games ----

    [Fact]
    public void Deleted_game_never_rolled()
    {
        foreach (var seed in s_seeds)
        {
            // Given a category with a deleted and a live game
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror").WithDeletedGame("Old Horror", 5, "Horror").WithGame("New Horror", 5, "Horror")
                .WithPlayers("Вася");

            s.Roll("Вася");

            // Then the live game is offered and the deleted one is not even a miss
            ScenarioAssert.Accepted(s);
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal(s.GameId("New Horror"), rolled.GameId);
            Assert.DoesNotContain(rolled.Misses, m => m.GameId == s.GameId("Old Horror"));
        }
    }

    [Fact]
    public void Only_deleted_games_left_rejects_with_no_available_games()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithDeletedGame("Old Horror", 5, "Horror")
            .WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }

    [Fact]
    public void Empty_pool_rejects_with_no_available_games()
    {
        var s = Scenario.New().WithCategory("Horror").WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    // ---- Misses: «Сейчас играет Вася», «Уже прошёл Вася» ----

    [Fact]
    public void Game_offered_to_another_player_is_a_being_played_miss()
    {
        var sawMiss = false;
        foreach (var seed in s_seeds)
        {
            // Given Петя rolled one of two horror games and has not started it yet
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
                .WithPlayers("Вася", "Петя")
                .Roll("Петя");
            ScenarioAssert.Accepted(s);
            var reserved = s.Player("Петя").Offer!.GameId;

            // When Вася rolls
            s.Roll("Вася");

            // Then Вася gets the other game; hitting Петя's game is logged as a BeingPlayed miss
            ScenarioAssert.Accepted(s);
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.NotEqual(reserved, rolled.GameId);
            Assert.All(rolled.Misses, m => Assert.Equal(new RollMiss(reserved, RollMissReason.BeingPlayed, s.PlayerId("Петя")), m));
            Assert.True(rolled.Misses.Count <= 1, "The same game is never missed twice in one roll.");
            sawMiss |= rolled.Misses.Count == 1;

            // And Петя's offer is untouched
            Assert.Equal(reserved, s.Player("Петя").Offer!.GameId);
        }

        Assert.True(sawMiss, "Over many seeds the wheel must sometimes land on the reserved game and log a miss.");
    }

    [Fact]
    public void Game_being_played_by_another_player_is_a_being_played_miss()
    {
        var sawMiss = false;
        foreach (var seed in s_seeds)
        {
            // Given Петя is playing one of two horror games
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
                .WithPlayers("Вася", "Петя")
                .Roll("Петя").Start("Петя");
            ScenarioAssert.Accepted(s);
            var playing = s.State.Runs[s.Player("Петя").ActiveRunId!.Value].GameId;

            s.Roll("Вася");

            ScenarioAssert.Accepted(s);
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.NotEqual(playing, rolled.GameId);
            Assert.All(rolled.Misses, m => Assert.Equal(new RollMiss(playing, RollMissReason.BeingPlayed, s.PlayerId("Петя")), m));
            sawMiss |= rolled.Misses.Count == 1;
        }

        Assert.True(sawMiss, "Over many seeds the wheel must sometimes land on the played game and log a miss.");
    }

    [Fact]
    public void Only_game_reserved_by_another_player_rejects_with_no_available_games()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася", "Петя")
            .Roll("Петя");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Game_completed_by_another_player_is_a_completed_in_season_miss()
    {
        var sawMiss = false;
        foreach (var seed in s_seeds)
        {
            // Given Петя completed the only horror game, then another horror game appears
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
                .WithPlayers("Вася", "Петя")
                .Roll("Петя").Start("Петя").Complete("Петя");
            ScenarioAssert.Accepted(s);
            s.WithGame("Alan Wake", 15, "Horror");

            s.Roll("Вася");

            ScenarioAssert.Accepted(s);
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal(s.GameId("Alan Wake"), rolled.GameId);
            Assert.All(
                rolled.Misses,
                m => Assert.Equal(new RollMiss(s.GameId("Silent Hill"), RollMissReason.CompletedInSeason, s.PlayerId("Петя")), m));
            sawMiss |= rolled.Misses.Count == 1;
        }

        Assert.True(sawMiss, "Over many seeds the wheel must sometimes land on the completed game and log a miss.");
    }

    [Fact]
    public void Game_completed_in_season_is_not_offered_again_even_to_its_player()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася").Complete("Вася");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    // ---- Phase and player checks ----

    [Fact]
    public void Roll_while_rolling_is_rejected_with_wrong_phase()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Roll_while_playing_is_rejected_with_wrong_phase()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Roll_by_unknown_player_is_rejected()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new RollGame(SequentialIds.Make(0x0BAD0000, 1))), RejectionCodes.PlayerUnknown);
    }

    [Fact]
    public void Player_can_roll_again_after_completing()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася").Complete("Вася");
        ScenarioAssert.Accepted(s);
        var first = s.Log.OfType<GameRolled>().Single().GameId;

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
        Assert.NotEqual(first, s.Player("Вася").Offer!.GameId);
    }
}
