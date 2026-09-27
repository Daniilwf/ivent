using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// Acceptance of docs/CONTENT.md «Примеры предметов» 1–10: «каждый пример из этого файла должен загружаться и работать
/// ровно как описано». Each item is published from the doc as written (<see cref="EconomyScenario.ExamplePack"/>), given by
/// the admin and used through the engine's commands. Plus SPEC «Окна использования» and «Эффекты от других применяются
/// только к будущим шагам цели, никогда к уже начатому». Pinned test ruleset: dice by hours = hours / 3 rounded, normal
/// difficulty = d4, a linear map start, c1, c2, …
/// </summary>
public class ContentItemAcceptanceTests
{
    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 15)];

    /// <summary>A running season with one Horror category of the given games (title, hours), the players and all the doc's items.</summary>
    private static Scenario Season(int seed, (string Title, decimal Hours)[] games, params string[] players)
    {
        var s = EconomyScenario.New(seed).WithCategory("Horror");
        foreach (var (title, hours) in games)
        {
            s.WithGame(title, hours, "Horror");
        }

        s.WithPlayers(players.Length == 0 ? ["Вася", "Петя"] : players);
        return s.WithContent(EconomyScenario.ExamplePack());
    }

    private static Scenario Season(params string[] players) =>
        Season(42, [("Silent Hill", 6), ("Alan Wake", 6), ("Dead Space", 6), ("Outlast", 6)], players);

    /// <summary>The player rolls the only game of the pool and starts it; returns the run.</summary>
    private static Guid PlayingOnlyGame(Scenario s, string player)
    {
        s.Roll(player).Start(player);
        return s.Player(player).ActiveRunId!.Value;
    }

    private static void RejectedWith(Scenario s, Func<Scenario, Scenario> act, string code) =>
        ScenarioAssert.RejectsWithoutChanges(s, act, code);

    // ---- 1. Апельсин: «Иди на 1 клетку вперёд» ----

    [Fact]
    public void Orange_moves_the_player_one_cell_forward_and_gives_no_points()
    {
        var s = Season();
        var orange = s.Give("Вася", "orange");

        s.Use("Вася", orange);

        ScenarioAssert.Accepted(s);
        var used = Assert.Single(s.LastEvents<ItemUsed>());
        Assert.Equal("orange", used.ObjectId);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(("start", "c1", 1, MoveReason.Item), (moved.From, moved.To, moved.Steps, moved.Reason));
        Assert.Equal("c1", s.Player("Вася").CellId);

        // Items that move give no points (SPEC «Броски предметов для перемещения очков не дают»)
        Assert.Empty(s.LastEvents<PointsChanged>());
        Assert.Equal(0, s.Player("Вася").Points);

        // An item is single-use: it leaves the inventory
        var removed = Assert.Single(s.LastEvents<ObjectRemoved>());
        Assert.Equal((orange, ObjectRemoval.Used), (removed.InstanceId, removed.Reason));
        Assert.DoesNotContain("orange", s.Inventory("Вася"));
    }

    [Fact]
    public void Orange_is_stackable_two_of_them_move_two_cells()
    {
        var s = Season();
        s.Give("Вася", "orange");
        s.Give("Вася", "orange");

        s.Used("Вася", "orange").Used("Вася", "orange");

        Assert.Equal(("c2", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
        Assert.Empty(s.Inventory("Вася"));
    }

    [Fact]
    public void A_used_orange_cannot_be_used_again()
    {
        var s = Season();
        var orange = s.Give("Вася", "orange");
        s.Use("Вася", orange);
        ScenarioAssert.Accepted(s);

        RejectedWith(s, x => x.Use("Вася", orange), RejectionCodes.ItemUnknown);
    }

    // ---- 2. Счастливый кубик: «Добавь 1d6 к следующему броску за прохождение» ----

    [Fact]
    public void Lucky_die_adds_1d6_to_the_next_completion_throw_points_and_steps()
    {
        // A 3-hour game: one d4 by hours
        var s = Season(42, [("Limbo", 3)]);
        var runId = PlayingOnlyGame(s, "Вася");
        s.Give("Вася", "lucky-die");

        s.Used("Вася", "lucky-die");

        var waiting = Assert.Single(s.Player("Вася").Wallet.NextDice);
        Assert.Equal(("lucky-die", DiceStage.Add), (waiting.ObjectId, waiting.Stage));
        Assert.Single(s.LastEvents<NextDiceModified>());

        // When he completes: the d4 and the d6 together make 4 + 1 in whatever order they are drawn
        s.NextRandom(4, 1).Complete("Вася");

        var run = s.State.Runs[runId];
        Assert.Equal(4, Assert.Single(run.Dice).Sides);
        Assert.InRange(run.DiceMods.Added, 1, 6);
        Assert.Equal(5, run.Dice.Sum(d => d.Value) + run.DiceMods.Added);

        // Then both points and position got the d6 (SPEC: кубы за прохождение меняют и очки, и позицию)
        Assert.Equal((5, "c5"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Empty(s.Player("Вася").Wallet.NextDice);
    }

    [Fact]
    public void Lucky_die_works_only_for_the_next_throw_the_one_after_is_plain()
    {
        var s = Season(42, [("Limbo", 3), ("Inside", 3)]);
        PlayingOnlyGame(s, "Вася");
        s.Give("Вася", "lucky-die");
        s.Used("Вася", "lucky-die");
        s.NextRandom(2, 2).Complete("Вася");
        Assert.Equal(4, s.Player("Вася").Points);

        var second = PlayingOnlyGame(s, "Вася");
        s.NextRandom(3).Complete("Вася");

        Assert.Equal(RunDiceMods.None, s.State.Runs[second].DiceMods);
        Assert.Equal((7, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Lucky_die_is_not_for_the_drop_penalty_it_keeps_waiting_for_a_completion()
    {
        var s = Season(42, [("Limbo", 3)]);
        PlayingOnlyGame(s, "Вася");
        s.Give("Вася", "lucky-die");
        s.Used("Вася", "lucky-die");
        s.Advance(TimeSpan.FromMinutes(61));

        s.NextRandom(2, 2).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        var dropped = Assert.Single(s.LastEvents<RunDropped>());
        Assert.Equal([new Die(4, 2), new Die(4, 2)], dropped.PenaltyDice);
        Assert.Equal("lucky-die", Assert.Single(s.Player("Вася").Wallet.NextDice).ObjectId);
    }

    [Fact]
    public void Lucky_die_is_not_stackable_a_second_one_is_refused_while_the_first_waits()
    {
        var s = Season(42, [("Limbo", 3)]);
        PlayingOnlyGame(s, "Вася");
        s.Give("Вася", "lucky-die");
        var second = s.Give("Вася", "lucky-die");
        s.Used("Вася", "lucky-die");

        RejectedWith(s, x => x.Use("Вася", second), RejectionCodes.ItemNotStackable);
    }

    // ---- 3. Переброс: «Перебрось кубы за прохождение, результат нового броска окончательный» ----

    [Fact]
    public void Reroll_dice_rerolls_the_throw_just_made_points_and_position_follow_the_new_dice()
    {
        // A 6-hour game: two d4
        var s = Season(42, [("Silent Hill", 6)]);
        var runId = PlayingOnlyGame(s, "Вася");
        s.NextRandom(1, 1).Complete("Вася");
        Assert.Equal((2, "c2"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        s.Give("Вася", "reroll-dice");

        s.NextRandom(4, 3).Used("Вася", "reroll-dice");

        var rerolled = Assert.Single(s.LastEvents<RunDiceRerolled>());
        Assert.Equal(runId, rerolled.RunId);
        Assert.Equal([3, 4], rerolled.Dice.Select(d => d.Value).Order());
        Assert.All(rerolled.Dice, d => Assert.Equal(4, d.Sides));
        Assert.Equal([3, 4], s.State.Runs[runId].Dice.Select(d => d.Value).Order());
        Assert.Equal((7, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Equal(5, s.LastEvents<PointsChanged>().Where(p => p.PlayerId == s.PlayerId("Вася")).Sum(p => p.Delta));
    }

    [Fact]
    public void Reroll_dice_result_is_final_even_when_it_is_worse()
    {
        var s = Season(42, [("Silent Hill", 6)]);
        PlayingOnlyGame(s, "Вася");
        s.NextRandom(4, 4).Complete("Вася");
        Assert.Equal((8, "c8"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        s.Give("Вася", "reroll-dice");

        s.NextRandom(1, 2).Used("Вася", "reroll-dice");

        Assert.Equal((3, "c3"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Reroll_dice_cannot_reroll_an_already_rerolled_throw()
    {
        var s = Season(42, [("Silent Hill", 6)]);
        PlayingOnlyGame(s, "Вася");
        s.NextRandom(1, 1).Complete("Вася");
        s.Give("Вася", "reroll-dice");
        var second = s.Give("Вася", "reroll-dice");
        s.NextRandom(4, 3).Used("Вася", "reroll-dice");
        var before = s.State;
        var log = s.Log.Count;

        s.NextRandom(4, 4).Use("Вася", second);

        // «результат нового броска окончательный»: which refusal is the engine's choice, but nothing changes
        Assert.False(s.Last.IsAccepted, "A rerolled throw is final; a second reroll must be refused.");
        Assert.Contains(
            s.Last.Rejection!.Code,
            new[] { RejectionCodes.ItemNotStackable, RejectionCodes.ItemNotApplicable, RejectionCodes.ItemWrongWindow });
        Assert.Equal(before, s.State);
        Assert.Equal(log, s.Log.Count);
        Assert.Equal((7, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    // ---- 4. Щит: «Следующий враждебный эффект против тебя не сработает» ----

    [Fact]
    public void Shield_gives_an_interception_effect_instead_of_the_item()
    {
        var s = Season();

        s.Give("Вася", "shield");
        s.Used("Вася", "shield");

        var given = Assert.Single(s.LastEvents<ObjectGiven>());
        Assert.Equal(("shield-effect", ObjectKind.Effect), (given.Item.ObjectId, given.Item.Kind));
        Assert.Equal(["shield-effect"], s.Inventory("Вася"));
    }

    [Fact]
    public void Shield_stops_the_next_hostile_effect_and_is_spent()
    {
        var s = Season().WithCoins("Вася", 10);
        s.Give("Вася", "shield");
        s.Used("Вася", "shield");
        var shield = s.Held("Вася", "shield-effect").InstanceId;
        s.Give("Петя", "bird-thief");

        s.Used("Петя", "bird-thief", target: "Вася");

        var stopped = Assert.Single(s.LastEvents<HostileIntercepted>());
        Assert.Equal(
            new HostileIntercepted(s.PlayerId("Вася"), shield, "bird-thief", s.PlayerId("Петя")),
            stopped);
        Assert.Equal((10, 0), (s.Player("Вася").Coins, s.Player("Петя").Coins));
        Assert.DoesNotContain("shield-effect", s.Inventory("Вася"));
        Assert.DoesNotContain("bird-thief", s.Inventory("Петя"));
    }

    [Fact]
    public void Shield_stops_only_one_hostile_effect_the_second_goes_through()
    {
        var s = Season().WithCoins("Вася", 10);
        s.Give("Вася", "shield");
        s.Used("Вася", "shield");
        s.Give("Петя", "bird-thief");
        s.Give("Петя", "bird-thief");
        s.Used("Петя", "bird-thief", target: "Вася");

        s.NextRandom(2).Used("Петя", "bird-thief", target: "Вася");

        Assert.Empty(s.LastEvents<HostileIntercepted>());
        Assert.Equal((8, 2), (s.Player("Вася").Coins, s.Player("Петя").Coins));
    }

    // ---- 5. Купон реролла: «Даёт один бесплатный реролл игры» ----

    [Fact]
    public void Reroll_coupon_gives_one_free_reroll_of_a_game()
    {
        var s = Season();
        s.Give("Вася", "reroll-coupon");

        s.Used("Вася", "reroll-coupon");

        var resource = Assert.Single(s.LastEvents<ResourceChanged>());
        Assert.Equal(("freeRerolls", 1), (resource.Resource, resource.Delta));
        Assert.Equal(1, s.Player("Вася").Resources["freeRerolls"]);

        // The free reroll of the roll first, then the coupon: no coins spent
        s.Roll("Вася");
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(RerollPayment.FreeRerollResource, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal((0, 0), (s.Player("Вася").Coins, s.Player("Вася").Resources["freeRerolls"]));
    }

    [Fact]
    public void Reroll_coupons_stack()
    {
        var s = Season();
        s.Give("Вася", "reroll-coupon");
        s.Give("Вася", "reroll-coupon");

        s.Used("Вася", "reroll-coupon").Used("Вася", "reroll-coupon");

        Assert.Equal(2, s.Player("Вася").Resources["freeRerolls"]);
    }

    // ---- 6. Выбор из трёх: «Следующий ролл покажет три игры, выберешь одну» ----

    [Fact]
    public void Pick_of_three_makes_the_next_roll_offer_three_games_to_choose_from()
    {
        foreach (var seed in s_seeds)
        {
            var s = Season(seed, [("Silent Hill", 6), ("Alan Wake", 6), ("Dead Space", 6), ("Outlast", 6), ("Soma", 6)]);
            s.Give("Вася", "pick-of-three");

            s.Used("Вася", "pick-of-three");
            Assert.Equal(3, Assert.Single(s.Player("Вася").Wallet.NextRoll).ChoiceCount);

            s.Roll("Вася");

            var rolled = Assert.Single(s.LastEvents<GameChoiceRolled>());
            Assert.Equal(3, rolled.Offers.Select(o => o.GameId).Distinct().Count());
            var choice = s.Player("Вася").Choice!;
            Assert.Equal(ChoiceKind.Game, choice.Kind);
            Assert.Equal(3, choice.Options.Count);

            // The player picks the second option: that game starts
            var picked = choice.Options[1].Game!.GameId;
            s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[1].Id));
            ScenarioAssert.Accepted(s);
            Assert.Equal(picked, s.State.Runs[s.Player("Вася").ActiveRunId!.Value].GameId);
        }
    }

    [Fact]
    public void Pick_of_three_is_for_one_roll_the_roll_after_offers_one_game()
    {
        var s = Season(42, [("Silent Hill", 6), ("Alan Wake", 6), ("Dead Space", 6), ("Outlast", 6), ("Soma", 6)]);
        s.Give("Вася", "pick-of-three");
        s.Used("Вася", "pick-of-three").Roll("Вася");
        var choice = s.Player("Вася").Choice!;
        s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id));
        s.Complete("Вася");

        s.Roll("Вася");

        Assert.Single(s.LastEvents<GameRolled>());
        Assert.Empty(s.LastEvents<GameChoiceRolled>());
        Assert.Null(s.Player("Вася").Choice);
    }

    // ---- 7. Короткая игра: «Следующий ролл — только игры короче 10 часов» ----

    private static readonly (string, decimal)[] s_mixedLengths =
        [("Limbo", 3), ("Inside", 9), ("Witcher", 50), ("Skyrim", 30), ("Persona", 80), ("Yakuza", 40)];

    [Fact]
    public void Short_game_limits_the_next_roll_to_games_under_10_hours()
    {
        foreach (var seed in s_seeds)
        {
            var s = Season(seed, s_mixedLengths);
            s.Give("Вася", "short-game");
            s.Used("Вася", "short-game");

            s.Roll("Вася");

            var offered = s.Player("Вася").Offer!;
            Assert.True(offered.Snapshot.Hours < 10, $"Seed {seed}: rolled {s.GameTitle(offered.GameId)} of {offered.Snapshot.Hours} h.");
        }
    }

    [Fact]
    public void Short_game_is_for_one_roll_later_rolls_see_long_games_again()
    {
        var longAfter = 0;
        foreach (var seed in s_seeds)
        {
            var s = Season(seed, s_mixedLengths);
            s.Give("Вася", "short-game");
            s.Used("Вася", "short-game").Roll("Вася").Start("Вася").Complete("Вася");

            s.Roll("Вася");

            longAfter += s.Player("Вася").Offer!.Snapshot.Hours >= 10 ? 1 : 0;
        }

        // Four of six games are long: over the seeds a plain roll lands on them
        Assert.True(longAfter > 0, "After the short roll, no later roll ever offered a long game.");
    }

    // ---- 8. Подлянка: «Выбери игрока с большим числом очков и жанр его следующей игры» ----

    private static readonly Dictionary<string, string> s_genres = new()
    {
        ["Silent Hill"] = "Horror",
        ["Alan Wake"] = "Horror",
        ["Dead Space"] = "Horror",
        ["Persona"] = "RPG",
        ["Fallout"] = "RPG",
    };

    /// <summary>Two categories, Horror (3 games) and RPG (2 games), and players Вася (5 points), Петя (10), Коля (2).</summary>
    private static Scenario TwoGenres(int seed)
    {
        var s = EconomyScenario.New(seed).WithCategory("Horror").WithCategory("RPG");
        foreach (var (title, genre) in s_genres)
        {
            s.WithGame(title, 6, genre);
        }

        s.WithPlayers("Вася", "Петя", "Коля").WithContent(EconomyScenario.ExamplePack());
        return s.WithPoints("Вася", 5).WithPoints("Петя", 10).WithPoints("Коля", 2);
    }

    private static string GenreOfOffer(Scenario s, string player) => s_genres[s.GameTitle(s.Player(player).Offer!.GameId)];

    [Fact]
    public void Dirty_trick_forces_the_chosen_genre_on_the_next_roll_of_a_player_with_more_points()
    {
        foreach (var seed in s_seeds)
        {
            var s = TwoGenres(seed);
            s.Give("Вася", "dirty-trick");

            s.Used("Вася", "dirty-trick", "Петя", "RPG");

            var used = Assert.Single(s.LastEvents<ItemUsed>());
            Assert.Equal([s.PlayerId("Петя")], used.Targets);
            Assert.Equal(["RPG"], used.Choices);
            var forced = s.Held("Петя", "forced-genre");
            Assert.Equal((ObjectKind.SpecialRoll, true, s.PlayerId("Вася")), (forced.Kind, forced.Hostile, forced.FromPlayerId));
            Assert.Equal(1, s.Player("Петя").Wallet.HostileReceived);

            s.Roll("Петя");

            Assert.Equal("RPG", GenreOfOffer(s, "Петя"));
            Assert.DoesNotContain("forced-genre", s.Inventory("Петя"));
        }
    }

    [Fact]
    public void Dirty_trick_forces_the_genre_only_once()
    {
        var horrorLater = 0;
        foreach (var seed in s_seeds)
        {
            var s = TwoGenres(seed);
            s.Give("Вася", "dirty-trick");
            s.Used("Вася", "dirty-trick", "Петя", "RPG").Roll("Петя").Start("Петя").Complete("Петя");

            s.Roll("Петя");

            horrorLater += GenreOfOffer(s, "Петя") == "Horror" ? 1 : 0;
        }

        Assert.True(horrorLater > 0, "The forced genre kept working after its one roll.");
    }

    [Fact]
    public void Dirty_trick_cannot_target_a_player_with_fewer_points()
    {
        var s = TwoGenres(42);
        var trick = s.Give("Вася", "dirty-trick");

        RejectedWith(s, x => x.Use("Вася", trick, "Коля", "RPG"), RejectionCodes.ItemInvalidTarget);
    }

    [Fact]
    public void Dirty_trick_cannot_target_a_player_with_as_many_points()
    {
        var s = TwoGenres(42).WithPoints("Петя", 5);
        var trick = s.Give("Вася", "dirty-trick");

        RejectedWith(s, x => x.Use("Вася", trick, "Петя", "RPG"), RejectionCodes.ItemInvalidTarget);
    }

    [Fact]
    public void Dirty_trick_cannot_target_oneself()
    {
        var s = TwoGenres(42);
        var trick = s.Give("Вася", "dirty-trick");

        RejectedWith(s, x => x.Use("Вася", trick, "Вася", "RPG"), RejectionCodes.ItemInvalidTarget);
    }

    [Fact]
    public void Dirty_trick_needs_a_chosen_target()
    {
        var s = TwoGenres(42);
        var trick = s.Give("Вася", "dirty-trick");

        RejectedWith(s, x => x.Use("Вася", trick, null, "RPG"), RejectionCodes.ItemTargetRequired);
    }

    [Fact]
    public void Dirty_trick_offers_only_the_categories_of_the_pool()
    {
        var s = TwoGenres(42);
        var trick = s.Give("Вася", "dirty-trick");

        RejectedWith(s, x => x.Use("Вася", trick, "Петя", "Racing"), RejectionCodes.ItemInvalidChoice);
    }

    // ---- «Эффекты от других применяются только к будущим шагам цели, никогда к уже начатому» ----

    [Fact]
    public void A_forced_genre_given_while_the_target_is_rolling_does_not_change_that_roll()
    {
        foreach (var seed in s_seeds)
        {
            var s = TwoGenres(seed);
            s.Roll("Петя");
            var offered = s.Player("Петя").Offer!.GameId;
            var other = GenreOfOffer(s, "Петя") == "Horror" ? "RPG" : "Horror";
            s.Give("Вася", "dirty-trick");

            s.Used("Вася", "dirty-trick", "Петя", other);

            Assert.Equal(offered, s.Player("Петя").Offer!.GameId);
            s.Start("Петя");
            Assert.Equal(offered, s.State.Runs[s.Player("Петя").ActiveRunId!.Value].GameId);

            // The next roll is the future step it applies to
            s.Complete("Петя").Roll("Петя");
            Assert.Equal(other, GenreOfOffer(s, "Петя"));
        }
    }

    [Fact]
    public void A_forced_genre_given_while_the_target_is_playing_does_not_change_the_run()
    {
        foreach (var seed in s_seeds)
        {
            var s = TwoGenres(seed);
            s.Roll("Петя").Start("Петя");
            var runId = s.Player("Петя").ActiveRunId!.Value;
            var game = s.State.Runs[runId].GameId;
            var other = s_genres[s.GameTitle(game)] == "Horror" ? "RPG" : "Horror";
            s.Give("Вася", "dirty-trick");

            s.Used("Вася", "dirty-trick", "Петя", other);

            Assert.Equal((runId, game), (s.Player("Петя").ActiveRunId!.Value, s.State.Runs[runId].GameId));
            s.Complete("Петя").Roll("Петя");
            Assert.Equal(other, GenreOfOffer(s, "Петя"));
        }
    }

    // ---- 9. Проклятие: «следующий бросок за прохождение уменьшится на 1d6, но не ниже 1» ----

    [Fact]
    public void Curse_puts_an_effect_on_the_chosen_player()
    {
        var s = Season();
        s.Give("Вася", "curse");

        s.Used("Вася", "curse", target: "Петя");

        var curse = s.Held("Петя", "curse-effect");
        Assert.Equal((ObjectKind.Effect, true, s.PlayerId("Вася")), (curse.Kind, curse.Hostile, curse.FromPlayerId));
        Assert.Single(s.LastEvents<HostileReceived>(), h => h.PlayerId == s.PlayerId("Петя") && h.ObjectId == "curse");
        Assert.DoesNotContain("curse", s.Inventory("Вася"));
    }

    [Fact]
    public void Curse_lowers_the_targets_next_completion_throw_by_1d6()
    {
        // A 6-hour game: two d4; the d4s and the d6 are all 4, so the order they are drawn in does not matter
        var s = Season(42, [("Silent Hill", 6)]);
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");
        PlayingOnlyGame(s, "Петя");

        s.NextRandom(4, 4, 4).Complete("Петя");

        Assert.Equal((4, "c4"), (s.Player("Петя").Points, s.Player("Петя").CellId));
        Assert.DoesNotContain("curse-effect", s.Inventory("Петя"));
        Assert.Contains(s.LastEvents<EffectTriggered>(), e => e.ObjectId == "curse-effect" && e.Trigger == Trigger.BeforeDice);
    }

    [Fact]
    public void Curse_never_takes_the_throw_below_1()
    {
        // A 3-hour game: one d4 of 2, the d6 of 2 → 0, raised to 1
        var s = Season(42, [("Limbo", 3)]);
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");
        PlayingOnlyGame(s, "Петя");

        s.NextRandom(2, 2).Complete("Петя");

        Assert.Equal((1, "c1"), (s.Player("Петя").Points, s.Player("Петя").CellId));
    }

    [Fact]
    public void Curse_is_spent_by_one_throw_the_next_one_is_plain()
    {
        var s = Season(42, [("Limbo", 3), ("Inside", 3)]);
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");
        PlayingOnlyGame(s, "Петя");
        s.NextRandom(2, 2).Complete("Петя");

        PlayingOnlyGame(s, "Петя");
        s.NextRandom(3).Complete("Петя");

        Assert.Equal((4, "c4"), (s.Player("Петя").Points, s.Player("Петя").CellId));
    }

    [Fact]
    public void Curse_does_not_touch_the_one_who_used_it()
    {
        var s = Season(42, [("Limbo", 3), ("Inside", 3)]);
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");
        PlayingOnlyGame(s, "Вася");

        s.NextRandom(3).Complete("Вася");

        Assert.Equal((3, "c3"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Curse_cannot_target_oneself()
    {
        var s = Season();
        var curse = s.Give("Вася", "curse");

        RejectedWith(s, x => x.Use("Вася", curse, "Вася"), RejectionCodes.ItemInvalidTarget);
    }

    // ---- 10. Птичкерс: «Укради у выбранного игрока 1d4 монеток» ----

    [Fact]
    public void Bird_thief_steals_1d4_coins_from_the_chosen_player()
    {
        var s = Season().WithCoins("Петя", 10);
        s.Give("Вася", "bird-thief");

        s.NextRandom(3).Used("Вася", "bird-thief", target: "Петя");

        var rolled = Assert.Single(s.LastEvents<EffectRolled>());
        Assert.Equal((3, new Die(4, 3)), (rolled.Total, Assert.Single(rolled.Dice)));
        Assert.Equal((3, 7), (s.Player("Вася").Coins, s.Player("Петя").Coins));
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Петя") && c.Delta == -3 && c.Reason == CoinsReason.Item);
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Вася") && c.Delta == 3 && c.Reason == CoinsReason.Item);

        // A hostile effect that reached its target is counted (SPEC «Админка считает, сколько враждебных эффектов получил каждый»)
        Assert.Equal(new HostileReceived(s.PlayerId("Петя"), s.PlayerId("Вася"), "bird-thief"), Assert.Single(s.LastEvents<HostileReceived>()));
        Assert.Equal(1, s.Player("Петя").Wallet.HostileReceived);
    }

    [Fact]
    public void Bird_thief_can_take_the_target_below_zero()
    {
        // SPEC «Минус: предметы и ивенты могут увести баланс в минус»
        var s = Season().WithCoins("Петя", 1);
        s.Give("Вася", "bird-thief");

        s.NextRandom(4).Used("Вася", "bird-thief", target: "Петя");

        Assert.Equal((4, -3), (s.Player("Вася").Coins, s.Player("Петя").Coins));
    }

    [Fact]
    public void Bird_thief_cannot_target_oneself()
    {
        var s = Season().WithCoins("Вася", 10);
        var thief = s.Give("Вася", "bird-thief");

        RejectedWith(s, x => x.Use("Вася", thief, "Вася"), RejectionCodes.ItemInvalidTarget);
    }

    [Fact]
    public void Bird_thief_needs_a_chosen_target()
    {
        var s = Season().WithCoins("Петя", 10);
        var thief = s.Give("Вася", "bird-thief");

        RejectedWith(s, x => x.Use("Вася", thief), RejectionCodes.ItemTargetRequired);
    }

    [Fact]
    public void An_item_of_another_player_cannot_be_used()
    {
        var s = Season().WithCoins("Петя", 10);
        var thief = s.Give("Вася", "bird-thief");

        var before = s.State;
        var log = s.Log.Count;
        s.Use("Петя", thief, "Вася");

        Assert.False(s.Last.IsAccepted, "Петя used an item from Вася's inventory.");
        Assert.Equal(before, s.State);
        Assert.Equal(log, s.Log.Count);
    }

    // ---- SPEC «Окна использования: перед роллом, после ролла, перед броском, после броска, в любой момент» ----

    [Fact]
    public void An_anytime_item_works_idle_rolling_and_playing()
    {
        var s = Season();
        s.Give("Вася", "orange");
        s.Give("Вася", "orange");
        s.Give("Вася", "orange");

        s.Used("Вася", "orange");
        s.Roll("Вася");
        s.Used("Вася", "orange");
        s.Start("Вася");
        s.Used("Вася", "orange");

        Assert.Equal("c3", s.Player("Вася").CellId);
    }

    [Fact]
    public void A_before_roll_item_is_refused_after_the_roll()
    {
        var s = Season();
        var pick = s.Give("Вася", "pick-of-three");
        s.Roll("Вася");

        RejectedWith(s, x => x.Use("Вася", pick), RejectionCodes.ItemWrongWindow);
    }

    [Fact]
    public void A_before_roll_item_is_refused_while_playing()
    {
        var s = Season();
        var shortGame = s.Give("Вася", "short-game");
        s.Roll("Вася").Start("Вася");

        RejectedWith(s, x => x.Use("Вася", shortGame), RejectionCodes.ItemWrongWindow);
    }

    [Fact]
    public void A_before_dice_item_is_refused_when_there_is_no_run_to_throw_for()
    {
        var s = Season();
        var lucky = s.Give("Вася", "lucky-die");

        RejectedWith(s, x => x.Use("Вася", lucky), RejectionCodes.ItemWrongWindow);
    }

    [Fact]
    public void A_before_dice_item_is_refused_while_the_game_is_only_offered()
    {
        var s = Season();
        var lucky = s.Give("Вася", "lucky-die");
        s.Roll("Вася");

        RejectedWith(s, x => x.Use("Вася", lucky), RejectionCodes.ItemWrongWindow);
    }

    [Fact]
    public void An_after_dice_item_is_refused_before_the_throw()
    {
        var s = Season();
        var reroll = s.Give("Вася", "reroll-dice");
        s.Roll("Вася").Start("Вася");

        RejectedWith(s, x => x.Use("Вася", reroll), RejectionCodes.ItemWrongWindow);
    }

    [Fact]
    public void An_after_dice_item_is_refused_when_nothing_was_thrown_yet()
    {
        var s = Season();
        var reroll = s.Give("Вася", "reroll-dice");

        s.ExpectRejection().Use("Вася", reroll);

        Assert.False(s.Last.IsAccepted, "Переброс without any throw was accepted.");
        Assert.Contains(s.Last.Rejection!.Code, new[] { RejectionCodes.ItemWrongWindow, RejectionCodes.ItemNotApplicable });
        Assert.Empty(s.Last.Events);
    }

    [Fact]
    public void Effects_and_special_rolls_are_not_used_by_hand()
    {
        var s = Season();
        s.Give("Вася", "shield");
        s.Used("Вася", "shield");
        var effect = s.Held("Вася", "shield-effect").InstanceId;

        RejectedWith(s, x => x.Use("Вася", effect), RejectionCodes.ItemNotAnItem);
    }
}
