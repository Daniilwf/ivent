using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// «Уже проходил» (SPEC «Статусы игры в сезоне»: free reroll, the game is excluded for you; D-05, D-07, D-08, D-92):
/// on the offered game or on any option of a pending choice, one command writes <see cref="GameExcluded"/> and at once a
/// new free roll. The excluded game never comes to that player again and is skipped silently (no miss), other players
/// still get it. Seed-dependent outcomes run over many seeds.
/// </summary>
public class AlreadyPlayedTests
{
    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 40)];

    private static readonly string[] s_horror = ["Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia"];

    private static Func<Ruleset, Ruleset> ChoiceOf(int count) =>
        r => r with { Roll = r.Roll with { ChoiceCount = count } };

    /// <summary>A Horror category of the given games (12 h each) and players Вася and Петя.</summary>
    private static Scenario Horror(int seed, int choiceCount, params string[] games)
    {
        var s = Scenario.New(seed: seed).WithRuleset(ChoiceOf(choiceCount)).WithCategory("Horror");
        foreach (var game in games)
        {
            s.WithGame(game, 12, "Horror");
        }

        return s.WithPlayers("Вася", "Петя");
    }

    private static Scenario Declare(Scenario s, string player, Guid game) =>
        s.Act(new DeclareAlreadyPlayed(s.PlayerId(player), game));

    private static Guid Offered(Scenario s, string player) => s.Player(player).Offer!.GameId;

    // ---- On the offered game ----

    [Fact]
    public void Already_played_on_the_offer_excludes_the_game_and_rolls_again_at_once()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася was offered one of three horror games
            var s = Horror(seed, 1, "Silent Hill", "Alan Wake", "Dead Space").Roll("Вася");
            var excluded = Offered(s, "Вася");
            s.Advance(TimeSpan.FromMinutes(5));

            // When he says he played it before the event
            Declare(s, "Вася", excluded);

            // Then exactly GameExcluded(AlreadyPlayed) and a new GameRolled of another game, rolled now
            ScenarioAssert.Accepted(s);
            Assert.Equal(2, s.Last.Events.Count);
            Assert.Equal(new GameExcluded(s.PlayerId("Вася"), excluded, ExclusionReason.AlreadyPlayed), s.Last.Events[0]);
            var rolled = Assert.IsType<GameRolled>(s.Last.Events[1]);
            Assert.Equal(s.PlayerId("Вася"), rolled.PlayerId);
            Assert.NotEqual(excluded, rolled.GameId);
            Assert.Equal(s.Clock.UtcNow, rolled.RolledAt);

            // And the excluded game is skipped silently: the dropped offer is not a miss «Сейчас играет Вася» either
            Assert.Empty(rolled.Misses);

            // And Вася is Rolling with the new game; the exclusion is stored
            var player = s.Player("Вася");
            Assert.Equal(TurnPhase.Rolling, player.Phase);
            Assert.Equal(rolled.GameId, player.Offer!.GameId);
            Assert.Null(player.Choice);
            Assert.Equal([new GameExclusion(excluded, ExclusionReason.AlreadyPlayed)], player.Exclusions);
        }
    }

    [Fact]
    public void Excluded_game_never_comes_to_the_player_again()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася excluded his first game
            var s = Horror(seed, 1, "Silent Hill", "Alan Wake", "Dead Space").Roll("Вася");
            var excluded = Offered(s, "Вася");
            Declare(s, "Вася", excluded);
            ScenarioAssert.Accepted(s);

            // When the admin keeps discarding his offer and he rolls again and again
            for (var i = 0; i < 10; i++)
            {
                s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сброс", DiscardOffer: true));
                ScenarioAssert.Accepted(s);
                s.Roll("Вася");

                // Then the excluded game is never offered and never even a miss (D-05: skipped silently)
                var rolled = Assert.Single(s.LastEvents<GameRolled>());
                Assert.NotEqual(excluded, rolled.GameId);
                Assert.DoesNotContain(excluded, rolled.Misses.Select(m => m.GameId));
            }

            Assert.Equal([new GameExclusion(excluded, ExclusionReason.AlreadyPlayed)], s.Player("Вася").Exclusions);
        }
    }

    [Fact]
    public void Excluded_game_still_comes_to_other_players()
    {
        foreach (var seed in s_seeds)
        {
            // Given two games; Вася excluded his first one and holds the other
            var s = Horror(seed, 1, "Silent Hill", "Alan Wake").Roll("Вася");
            var excluded = Offered(s, "Вася");
            Declare(s, "Вася", excluded);
            ScenarioAssert.Accepted(s);
            var kept = Offered(s, "Вася");

            // When Петя rolls
            s.Roll("Петя");

            // Then he gets the game Вася excluded: the exclusion is personal
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal(excluded, rolled.GameId);
            Assert.All(rolled.Misses, m => Assert.Equal(new RollMiss(kept, RollMissReason.BeingPlayed, s.PlayerId("Вася")), m));
            Assert.Empty(s.Player("Петя").Exclusions);
        }
    }

    [Fact]
    public void Already_played_on_the_only_game_frees_it_for_others()
    {
        // Given the pool has one game, offered to Вася, who played it before
        var s = Horror(42, 1, "Silent Hill").Roll("Вася");
        Declare(s, "Вася", s.GameId("Silent Hill"));
        ScenarioAssert.Accepted(s);

        // When Петя rolls, the game is free: no miss «Сейчас играет Вася»
        s.Roll("Петя");

        var rolled = Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal(s.GameId("Silent Hill"), rolled.GameId);
        Assert.Empty(rolled.Misses);
    }

    // ---- Free: not a reroll, spends nothing (D-07), no limit (D-08) ----

    [Fact]
    public void Already_played_spends_nothing()
    {
        // Given Вася has coins and free reroll coupons
        var s = Horror(42, 1, s_horror);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Стартовый капитал", CoinsDelta: 7, ResourceDeltas: [new ResourceDelta("freeRerolls", 2)]));
        ScenarioAssert.Accepted(s);
        s.Roll("Вася");
        var before = s.Player("Вася");

        // When he declares «Уже проходил»
        Declare(s, "Вася", Offered(s, "Вася"));

        // Then coins, resources, points and position are untouched, and only the two roll events are written
        ScenarioAssert.Accepted(s);
        var after = s.Player("Вася");
        Assert.Equal(before.Coins, after.Coins);
        Assert.Equal(before.Resources, after.Resources);
        Assert.Equal(before.Points, after.Points);
        Assert.Equal(before.CellId, after.CellId);
        Assert.Equal(before.Path, after.Path);
        Assert.All(s.Last.Events, e => Assert.True(e is GameExcluded or GameRolled, $"Unexpected {e.GetType().Name}."));
    }

    [Fact]
    public void Already_played_has_no_limit()
    {
        foreach (var seed in s_seeds)
        {
            // Given five games: Вася declares «Уже проходил» four times in a row
            var s = Horror(seed, 1, s_horror).Roll("Вася");
            var declared = new List<Guid>();
            for (var i = 0; i < 4; i++)
            {
                var game = Offered(s, "Вася");
                declared.Add(game);
                Declare(s, "Вася", game);
                ScenarioAssert.Accepted(s);
                Assert.Equal(2, s.Last.Events.Count);
            }

            // Then every one is accepted and excluded; the fifth game is offered; nothing was spent
            var player = s.Player("Вася");
            Assert.Equal(declared.Order(), player.Exclusions.Select(x => x.GameId));
            Assert.DoesNotContain(player.Offer!.GameId, declared);
            Assert.Equal(0, player.Coins);
        }
    }

    // ---- Nothing left to roll ----

    [Fact]
    public void Already_played_when_no_other_game_is_available_only_excludes_and_leaves_the_player_idle()
    {
        var s = Horror(42, 1, "Silent Hill").Roll("Вася");

        Declare(s, "Вася", s.GameId("Silent Hill"));

        // The command is accepted with the exclusion alone (D-92)
        ScenarioAssert.Accepted(s);
        Assert.Equal([new GameExcluded(s.PlayerId("Вася"), s.GameId("Silent Hill"), ExclusionReason.AlreadyPlayed)], s.Last.Events);
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Idle, player.Phase);
        Assert.Null(player.Offer);
        Assert.Null(player.Choice);
        Assert.Equal([new GameExclusion(s.GameId("Silent Hill"), ExclusionReason.AlreadyPlayed)], player.Exclusions);

        // And the next roll is refused: the pool has nothing for him
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Already_played_when_the_other_games_are_busy_only_excludes()
    {
        // Given Петя holds one of two games and Вася was offered the other
        var s = Horror(42, 1, "Silent Hill", "Alan Wake").Roll("Петя").Roll("Вася");

        // When Вася declares his game
        Declare(s, "Вася", Offered(s, "Вася"));

        // Then there is nothing to roll: only the exclusion, Вася is idle
        ScenarioAssert.Accepted(s);
        Assert.IsType<GameExcluded>(Assert.Single(s.Last.Events));
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }

    // ---- On an option of a pending choice ----

    [Fact]
    public void Already_played_on_an_option_rolls_a_whole_new_choice_without_that_game()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася waits for a choice of 3 out of 5 horror games
            var s = Horror(seed, 3, s_horror).Roll("Вася");
            var oldChoice = s.Player("Вася").Choice!;
            var excluded = oldChoice.Options[1].Game!.GameId;

            // When he declares one option «Уже проходил»
            Declare(s, "Вася", excluded);

            // Then the game is excluded and the choice is rolled anew as a whole: 3 of the 4 other games
            ScenarioAssert.Accepted(s);
            Assert.Equal(2, s.Last.Events.Count);
            Assert.Equal(new GameExcluded(s.PlayerId("Вася"), excluded, ExclusionReason.AlreadyPlayed), s.Last.Events[0]);
            var rolled = Assert.IsType<GameChoiceRolled>(s.Last.Events[1]);
            Assert.NotEqual(oldChoice.ChoiceId, rolled.ChoiceId);
            Assert.Equal(3, rolled.Offers.Count);
            Assert.DoesNotContain(excluded, rolled.Offers.Select(o => o.GameId));

            // And the old options were released first: none of them is a miss, the excluded game is not a miss either
            Assert.Empty(rolled.Misses);

            var player = s.Player("Вася");
            Assert.Equal(TurnPhase.Rolling, player.Phase);
            Assert.Null(player.Offer);
            Assert.Equal(rolled.ChoiceId, player.Choice!.ChoiceId);
            Assert.Equal(
                rolled.Offers.Select(o => o.GameId).Order(),
                player.Choice.Options.Select(o => o.Game!.GameId).Order());
            Assert.Equal([new GameExclusion(excluded, ExclusionReason.AlreadyPlayed)], player.Exclusions);
        }
    }

    [Fact]
    public void Already_played_on_an_option_when_one_game_is_left_is_a_plain_roll()
    {
        // Given a choice of both games of the pool
        var s = Horror(42, 3, "Silent Hill", "Alan Wake").Roll("Вася");
        Assert.NotNull(s.Player("Вася").Choice);

        Declare(s, "Вася", s.GameId("Alan Wake"));

        // Then the remaining game is simply offered (D-91: one game → GameRolled)
        ScenarioAssert.Accepted(s);
        Assert.IsType<GameExcluded>(s.Last.Events[0]);
        var rolled = Assert.IsType<GameRolled>(s.Last.Events[1]);
        Assert.Equal(s.GameId("Silent Hill"), rolled.GameId);
        Assert.Null(s.Player("Вася").Choice);
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Вася").Offer!.GameId);
    }

    [Fact]
    public void Already_played_on_an_option_when_nothing_is_left_leaves_the_player_idle()
    {
        // Given Петя holds one game and Вася's choice has the other two
        var s = Horror(42, 3, "Silent Hill").Roll("Петя");
        s.WithGame("Alan Wake", 12, "Horror").WithGame("Dead Space", 12, "Horror").Roll("Вася");
        Assert.NotNull(s.Player("Вася").Choice);

        // When Вася excludes one option, the remaining option is free again, so a plain roll offers it
        Declare(s, "Вася", s.GameId("Dead Space"));

        ScenarioAssert.Accepted(s);
        var rolled = Assert.IsType<GameRolled>(s.Last.Events[1]);
        Assert.Equal(s.GameId("Alan Wake"), rolled.GameId);
        Assert.All(rolled.Misses, m => Assert.Equal(s.GameId("Silent Hill"), m.GameId));

        // When he excludes that one too, nothing is left: only the exclusion
        Declare(s, "Вася", s.GameId("Alan Wake"));

        ScenarioAssert.Accepted(s);
        Assert.IsType<GameExcluded>(Assert.Single(s.Last.Events));
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        Assert.Null(s.Player("Вася").Choice);
    }

    // ---- Refused ----

    [Fact]
    public void Game_that_is_not_offered_is_rejected()
    {
        var s = Horror(42, 1, "Silent Hill", "Alan Wake", "Dead Space").Roll("Вася");
        var other = s_horror.Take(3).Select(s.GameId).First(g => g != Offered(s, "Вася"));

        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", other), RejectionCodes.GameNotOffered);
        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", SequentialIds.Make(0x0BAD0000, 1)), RejectionCodes.GameNotOffered);
        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", Guid.Empty), RejectionCodes.GameNotOffered);
    }

    [Fact]
    public void Game_offered_to_another_player_is_rejected()
    {
        var s = Horror(42, 1, "Silent Hill", "Alan Wake").Roll("Петя").Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", Offered(x, "Петя")), RejectionCodes.GameNotOffered);
        Assert.Empty(s.Player("Петя").Exclusions);
    }

    [Fact]
    public void Game_that_is_not_an_option_of_the_pending_choice_is_rejected()
    {
        var s = Horror(42, 3, "Silent Hill", "Alan Wake", "Dead Space", "Outlast").Roll("Вася");
        var choice = s.Player("Вася").Choice!;
        var notOption = s_horror.Take(4).Select(s.GameId).Single(g => choice.Options.All(o => o.Game!.GameId != g));

        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", notOption), RejectionCodes.GameNotOffered);
        Assert.Equal(choice, s.Player("Вася").Choice);
    }

    [Fact]
    public void Already_played_while_idle_is_rejected_with_wrong_phase()
    {
        var s = Horror(42, 1, "Silent Hill");

        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", x.GameId("Silent Hill")), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Already_played_on_the_game_being_played_is_rejected_with_wrong_phase()
    {
        // Once started, «Уже проходил» is too late: that is a drop or a tech reroll (C6)
        var s = Horror(42, 1, "Silent Hill", "Alan Wake").Roll("Вася").Start("Вася");
        var playing = s.State.Runs[s.Player("Вася").ActiveRunId!.Value].GameId;

        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", playing), RejectionCodes.WrongPhase);
        Assert.Empty(s.Player("Вася").Exclusions);
    }

    [Fact]
    public void Already_played_by_an_unknown_player_is_rejected()
    {
        var s = Horror(42, 1, "Silent Hill").Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new DeclareAlreadyPlayed(SequentialIds.Make(0x0BAD0000, 1), Offered(x, "Вася"))),
            RejectionCodes.PlayerUnknown);
    }

    [Fact]
    public void Already_played_in_a_season_that_is_not_running_is_rejected()
    {
        // Closing: rolls are forbidden, so is a free roll (SPEC «Сезон»)
        var s = Horror(42, 1, "Silent Hill", "Alan Wake").Roll("Вася");
        var offered = Offered(s, "Вася");
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", offered), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Already_played_in_a_draft_season_is_rejected()
    {
        var s = Scenario.New().AsDraft().WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", x.GameId("Silent Hill")), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Declaring_twice_from_two_tabs_is_rejected_the_second_time()
    {
        // The second tab still shows the old offer; it is no longer offered
        var s = Horror(42, 1, "Silent Hill", "Alan Wake", "Dead Space").Roll("Вася");
        var first = Offered(s, "Вася");
        Declare(s, "Вася", first);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Declare(x, "Вася", first), RejectionCodes.GameNotOffered);
    }

    // ---- Own exclusions are hidden (D-05, D-57): not on the wheel, not a miss ----

    [Fact]
    public void Category_whose_only_game_is_excluded_is_not_on_the_wheel_for_that_player()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася excluded the only horror game, then a light puzzle category appears
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror", weight: 1000).WithGame("Silent Hill", 12, "Horror")
                .WithPlayers("Вася", "Петя")
                .Roll("Вася");
            Declare(s, "Вася", s.GameId("Silent Hill"));
            ScenarioAssert.Accepted(s);
            s.WithCategory("Puzzle", weight: 1).WithGame("Tetris", 2, "Puzzle");

            // When Вася rolls, the heavy horror category is not on his wheel
            s.Roll("Вася");

            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.Equal("Puzzle", rolled.Category);
            Assert.Equal(s.GameId("Tetris"), rolled.GameId);
            Assert.Empty(rolled.Misses);
        }
    }

    [Fact]
    public void Category_whose_other_games_are_busy_is_not_on_the_wheel_for_the_excluding_player()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася excluded one horror game and Петя holds the other one
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror", weight: 1000).WithGame("Silent Hill", 12, "Horror")
                .WithPlayers("Вася", "Петя")
                .Roll("Вася");
            Declare(s, "Вася", s.GameId("Silent Hill"));
            ScenarioAssert.Accepted(s);
            s.Roll("Петя");
            Assert.Equal(s.GameId("Silent Hill"), Offered(s, "Петя"));
            s.WithGame("Alan Wake", 12, "Horror");
            s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Сброс", DiscardOffer: true));
            s.Roll("Петя");
            ScenarioAssert.Accepted(s);
            var petyaGame = Offered(s, "Петя");
            s.WithCategory("Puzzle", weight: 1).WithGame("Tetris", 2, "Puzzle");

            // When Вася rolls
            s.Roll("Вася");
            var rolled = Assert.Single(s.LastEvents<GameRolled>());

            if (petyaGame == s.GameId("Alan Wake"))
            {
                // Then horror has only his excluded game and Петя's game: no available game, not on his wheel
                Assert.Equal("Puzzle", rolled.Category);
                Assert.Empty(rolled.Misses);
            }
            else
            {
                // Петя got Silent Hill: Alan Wake is available for Вася; Silent Hill is skipped silently, never a miss
                Assert.DoesNotContain(s.GameId("Silent Hill"), rolled.Misses.Select(m => m.GameId));
                Assert.NotEqual(s.GameId("Silent Hill"), rolled.GameId);
            }
        }
    }

    [Fact]
    public void Excluded_game_is_skipped_without_a_miss_while_others_are_logged()
    {
        var sawOtherMiss = false;
        foreach (var seed in s_seeds)
        {
            // Given Вася excluded Silent Hill, Петя holds another horror game, two more are free
            var s = Horror(seed, 1, "Silent Hill").Roll("Вася");
            Declare(s, "Вася", s.GameId("Silent Hill"));
            s.WithGame("Alan Wake", 12, "Horror").Roll("Петя");
            var petyaGame = Offered(s, "Петя");
            s.WithGame("Dead Space", 12, "Horror").WithGame("Outlast", 12, "Horror");

            s.Roll("Вася");

            // Then Petya's game may be a miss «Сейчас играет Петя», the excluded one never is
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.NotEqual(s.GameId("Silent Hill"), rolled.GameId);
            Assert.NotEqual(petyaGame, rolled.GameId);
            Assert.DoesNotContain(s.GameId("Silent Hill"), rolled.Misses.Select(m => m.GameId));
            sawOtherMiss |= rolled.Misses.Any(m => m.GameId == petyaGame);
        }

        Assert.True(sawOtherMiss, "Over many seeds the draw must sometimes hit Петя's game and log it.");
    }

    // ---- Log and state ----

    [Fact]
    public void Replaying_the_log_gives_the_same_exclusions()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            var s = Horror(seed, 1, s_horror).Roll("Вася");
            Declare(s, "Вася", Offered(s, "Вася"));
            Declare(s, "Вася", Offered(s, "Вася"));
            s.Roll("Петя");
            Declare(s, "Петя", Offered(s, "Петя"));
            ScenarioAssert.Accepted(s);

            var replayed = SeasonEngine.Replay(s.Log);

            Assert.Equal(s.State, replayed);
            Assert.Equal(2, replayed.Players[s.PlayerId("Вася")].Exclusions.Count);
            Assert.Equal(s.Player("Вася").Exclusions, replayed.Players[s.PlayerId("Вася")].Exclusions);
            Assert.Equal(s.Player("Петя").Exclusions, replayed.Players[s.PlayerId("Петя")].Exclusions);
        }
    }

    [Fact]
    public void Exclusions_are_kept_ordered_by_game_id()
    {
        var sawUnordered = false;
        foreach (var seed in s_seeds)
        {
            // Given Вася excludes three games in whatever order the wheel offers them
            var s = Horror(seed, 1, s_horror).Roll("Вася");
            var declared = new List<Guid>();
            for (var i = 0; i < 3; i++)
            {
                declared.Add(Offered(s, "Вася"));
                Declare(s, "Вася", declared[^1]);
                ScenarioAssert.Accepted(s);
            }

            // Then they are stored one per game, ordered by game id
            var exclusions = s.Player("Вася").Exclusions.Select(x => x.GameId).ToList();
            Assert.Equal(declared.Order(), exclusions);
            sawUnordered |= !declared.SequenceEqual(declared.Order());
        }

        Assert.True(sawUnordered, "Over many seeds some declarations must come out of id order.");
    }

    [Fact]
    public void Same_seed_gives_the_same_new_roll()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            Scenario Play()
            {
                var s = Horror(seed, 1, s_horror).Roll("Вася");
                return Declare(s, "Вася", Offered(s, "Вася"));
            }

            Assert.Equal(Play().Log, Play().Log);
        }
    }
}
