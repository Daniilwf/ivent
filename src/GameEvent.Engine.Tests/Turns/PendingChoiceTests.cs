using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Turns;

/// <summary>
/// Waiting for a choice (SPEC «Игровой цикл»: one general state kept on the server, a closed tab breaks nothing;
/// K-5, D-06, D-46, D-89, D-91). The first kind is the choice of N games: a roll with <c>roll.choiceCount</c> = N
/// draws without replacement up to N available games of one category, logs misses, reserves every option;
/// <see cref="MakeChoice"/> turns the picked one into the offer and frees the others.
/// Randomness is not scripted: outcomes must hold for every seed, so seed-dependent tests run many seeds.
/// </summary>
public class PendingChoiceTests
{
    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 40)];

    private static readonly string[] s_horror = ["Silent Hill", "Alan Wake", "Dead Space", "Outlast"];
    private static readonly string[] s_puzzle = ["Tetris", "Portal", "Baba Is You", "The Witness"];

    private static Func<Ruleset, Ruleset> ChoiceOf(int count) =>
        r => r with { Roll = r.Roll with { ChoiceCount = count } };

    /// <summary>A season with choice of <paramref name="count"/> and a Horror category of the given games (12 h each).</summary>
    private static Scenario Horror(int count, int seed, params string[] games)
    {
        var s = Scenario.New(seed: seed).WithRuleset(ChoiceOf(count)).WithCategory("Horror");
        foreach (var game in games)
        {
            s.WithGame(game, 12, "Horror");
        }

        return s.WithPlayers("Вася", "Петя");
    }

    private static Scenario Horror(params string[] games) => Horror(3, seed: 42, games);

    private static RunSnapshot SnapshotFor(Scenario s, decimal? hours) =>
        new(s.Ruleset.Version, hours, s.Ruleset.Reward.DiceCount, s.Ruleset.Reward.DieByDifficulty);

    private static string OptionId(Scenario s, string title) => s.GameId(title).ToString("N");

    private static GameChoiceRolled ChoiceRoll(Scenario s)
    {
        ScenarioAssert.Accepted(s);
        return Assert.IsType<GameChoiceRolled>(Assert.Single(s.Last.Events));
    }

    private static Scenario Choose(Scenario s, string player, string title) =>
        s.Act(new MakeChoice(s.PlayerId(player), s.Player(player).Choice!.ChoiceId, OptionId(s, title)));

    // ---- The roll with a choice ----

    [Fact]
    public void Roll_offers_up_to_choice_count_distinct_available_games_of_one_category()
    {
        foreach (var seed in s_seeds)
        {
            // Given two categories with four games each and choice of 3
            var s = Scenario.New(seed: seed).WithRuleset(ChoiceOf(3)).WithCategory("Horror").WithCategory("Puzzle");
            foreach (var game in s_horror)
            {
                s.WithGame(game, 12, "Horror");
            }

            foreach (var game in s_puzzle)
            {
                s.WithGame(game, 6, "Puzzle");
            }

            s.WithPlayers("Вася").Advance(TimeSpan.FromHours(2));
            var rolledAt = s.Clock.UtcNow;

            // When
            s.Roll("Вася");

            // Then one GameChoiceRolled: 3 different games of the rolled category, no misses, roll-time snapshots
            var rolled = ChoiceRoll(s);
            Assert.Equal(s.PlayerId("Вася"), rolled.PlayerId);
            Assert.Empty(rolled.Misses);
            Assert.NotEqual(Guid.Empty, rolled.ChoiceId);
            Assert.Equal(3, rolled.Offers.Count);
            Assert.Equal(3, rolled.Offers.Select(o => o.GameId).Distinct().Count());
            var (category, hours) = rolled.Category == "Horror" ? (s_horror, 12m) : (s_puzzle, 6m);
            Assert.Contains(rolled.Category, new[] { "Horror", "Puzzle" });
            Assert.All(rolled.Offers, o =>
            {
                Assert.Contains(s.GameTitle(o.GameId), category);
                Assert.Equal(SnapshotFor(s, hours), o.Snapshot);
                Assert.Equal(rolledAt, o.RolledAt);
            });

            // And the player is Rolling and waits for a choice of those games; nothing is offered or started yet
            var player = s.Player("Вася");
            Assert.Equal(TurnPhase.Rolling, player.Phase);
            Assert.Null(player.Offer);
            Assert.Null(player.ActiveRunId);
            Assert.Empty(s.State.Runs);
            var choice = Assert.IsType<PendingChoice>(player.Choice);
            Assert.Equal(rolled.ChoiceId, choice.ChoiceId);
            Assert.Equal(ChoiceKind.Game, choice.Kind);
            Assert.Equal(
                rolled.Offers.Select(o => new ChoiceOption(o.GameId.ToString("N"), o)).OrderBy(o => o.Id, StringComparer.Ordinal),
                choice.Options.OrderBy(o => o.Id, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Roll_offers_every_available_game_when_the_category_has_fewer_than_choice_count()
    {
        var s = Horror(3, seed: 42, "Silent Hill", "Alan Wake");

        s.Roll("Вася");

        var rolled = ChoiceRoll(s);
        Assert.Equal(
            new[] { s.GameId("Silent Hill"), s.GameId("Alan Wake") }.Order(),
            rolled.Offers.Select(o => o.GameId).Order());
        Assert.Equal(2, s.Player("Вася").Choice!.Options.Count);
    }

    [Fact]
    public void Roll_with_only_one_available_game_is_a_plain_roll()
    {
        // Choice of 3, but the pool has one game: nothing to choose from (D-91)
        var s = Horror("Silent Hill");

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        var rolled = Assert.IsType<GameRolled>(Assert.Single(s.Last.Events));
        Assert.Equal(s.GameId("Silent Hill"), rolled.GameId);
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Rolling, player.Phase);
        Assert.Null(player.Choice);
        Assert.Equal(s.GameId("Silent Hill"), player.Offer!.GameId);
    }

    [Fact]
    public void Roll_with_choice_count_one_is_a_plain_roll()
    {
        // N = 1 leaves the log as before C4 (D-91)
        var s = Horror(1, seed: 42, s_horror);

        s.Roll("Вася");

        ScenarioAssert.Accepted(s);
        Assert.IsType<GameRolled>(Assert.Single(s.Last.Events));
        Assert.Null(s.Player("Вася").Choice);
        Assert.NotNull(s.Player("Вася").Offer);
    }

    [Fact]
    public void Game_offered_to_another_player_is_a_logged_miss_and_the_draw_continues()
    {
        // Given Петя holds the only horror game, then two more arrive in the pool
        var s = Horror("Silent Hill");
        s.Roll("Петя");
        s.WithGame("Alan Wake", 12, "Horror").WithGame("Dead Space", 12, "Horror");

        // When Вася rolls with choice of 3, every game of the category is drawn
        s.Roll("Вася");

        // Then the busy game is a miss «Сейчас играет Петя», the other two are the options
        var rolled = ChoiceRoll(s);
        Assert.Equal([new RollMiss(s.GameId("Silent Hill"), RollMissReason.BeingPlayed, s.PlayerId("Петя"))], rolled.Misses);
        Assert.Equal(
            new[] { s.GameId("Alan Wake"), s.GameId("Dead Space") }.Order(),
            rolled.Offers.Select(o => o.GameId).Order());
    }

    [Fact]
    public void Game_completed_in_the_season_is_a_logged_miss_and_never_an_option()
    {
        var s = Horror("Silent Hill");
        s.Roll("Петя").Start("Петя").Complete("Петя");
        s.WithGame("Alan Wake", 12, "Horror").WithGame("Dead Space", 12, "Horror");

        s.Roll("Вася");

        var rolled = ChoiceRoll(s);
        Assert.Equal([new RollMiss(s.GameId("Silent Hill"), RollMissReason.CompletedInSeason, s.PlayerId("Петя"))], rolled.Misses);
        Assert.DoesNotContain(s.GameId("Silent Hill"), rolled.Offers.Select(o => o.GameId));
    }

    [Fact]
    public void Deleted_game_is_never_an_option()
    {
        foreach (var seed in s_seeds)
        {
            var s = Scenario.New(seed: seed).WithRuleset(ChoiceOf(3))
                .WithCategory("Horror")
                .WithGame("Silent Hill", 12, "Horror")
                .WithDeletedGame("Gone", 12, "Horror")
                .WithGame("Alan Wake", 12, "Horror")
                .WithPlayers("Вася");

            s.Roll("Вася");

            var rolled = ChoiceRoll(s);
            Assert.DoesNotContain(s.GameId("Gone"), rolled.Offers.Select(o => o.GameId));
            Assert.DoesNotContain(s.GameId("Gone"), rolled.Misses.Select(m => m.GameId));
            Assert.Equal(2, rolled.Offers.Count);
        }
    }

    // ---- Reservation (D-06) ----

    [Fact]
    public void Every_option_is_reserved_until_the_choice_is_made()
    {
        foreach (var seed in s_seeds)
        {
            // Given Вася waits for a choice of 3 out of 4 horror games
            var s = Horror(3, seed, s_horror);
            s.Roll("Вася");
            var options = ChoiceRoll(s).Offers.Select(o => o.GameId).ToHashSet();

            // When Петя rolls
            s.Roll("Петя");

            // Then he gets the fourth game; each of Вася's options is a miss «Сейчас играет Вася»
            ScenarioAssert.Accepted(s);
            var rolled = Assert.IsType<GameRolled>(Assert.Single(s.Last.Events));
            Assert.DoesNotContain(rolled.GameId, options);
            Assert.Equal(options.Order(), rolled.Misses.Select(m => m.GameId).Order());
            Assert.All(rolled.Misses, m =>
            {
                Assert.Equal(RollMissReason.BeingPlayed, m.Reason);
                Assert.Equal(s.PlayerId("Вася"), m.ByPlayerId);
            });
        }
    }

    [Fact]
    public void Roll_is_rejected_when_every_game_is_among_another_players_options()
    {
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        ChoiceRoll(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Петя"), RejectionCodes.NoAvailableGames);
    }

    // ---- MakeChoice ----

    [Fact]
    public void Making_a_choice_offers_the_chosen_game_with_its_roll_time_snapshot()
    {
        // Given Вася rolled a choice of three games an hour ago
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var rolled = ChoiceRoll(s);
        var chosen = rolled.Offers.Single(o => o.GameId == s.GameId("Alan Wake"));
        s.Advance(TimeSpan.FromHours(1));

        // When he picks Alan Wake
        Choose(s, "Вася", "Alan Wake");

        // Then one ChoiceMade, the game is offered exactly as rolled, the choice is gone
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new ChoiceMade(s.PlayerId("Вася"), rolled.ChoiceId, OptionId(s, "Alan Wake"))],
            s.Last.Events);
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Rolling, player.Phase);
        Assert.Null(player.Choice);
        Assert.Equal(chosen, player.Offer);
        Assert.Null(player.ActiveRunId);
    }

    [Fact]
    public void Making_a_choice_frees_the_other_options()
    {
        // Given Вася picked Alan Wake out of all three horror games
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        Choose(s, "Вася", "Alan Wake");
        ScenarioAssert.Accepted(s);

        // When Петя rolls with choice of 3, every game of the category is drawn
        s.Roll("Петя");

        // Then the other two are free again; the chosen one is still busy
        var rolled = ChoiceRoll(s);
        Assert.Equal([new RollMiss(s.GameId("Alan Wake"), RollMissReason.BeingPlayed, s.PlayerId("Вася"))], rolled.Misses);
        Assert.Equal(
            new[] { s.GameId("Silent Hill"), s.GameId("Dead Space") }.Order(),
            rolled.Offers.Select(o => o.GameId).Order());
    }

    [Fact]
    public void Start_after_choosing_plays_the_chosen_game()
    {
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var chosen = ChoiceRoll(s).Offers.Single(o => o.GameId == s.GameId("Dead Space"));
        Choose(s, "Вася", "Dead Space");

        s.Start("Вася");

        ScenarioAssert.Accepted(s);
        var started = Assert.IsType<RunStarted>(Assert.Single(s.Last.Events));
        Assert.Equal(s.GameId("Dead Space"), started.GameId);
        Assert.Equal(chosen.Snapshot, started.Snapshot);
        Assert.Equal(chosen.RolledAt, started.RolledAt);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
        Assert.Null(s.Player("Вася").Offer);
    }

    [Fact]
    public void Ruleset_change_while_choosing_does_not_change_the_snapshot()
    {
        // Snapshot: everything about a run is fixed at roll time (SPEC «Уточнения»: Сезон)
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var chosen = ChoiceRoll(s).Offers.Single(o => o.GameId == s.GameId("Silent Hill"));
        s.WithRuleset(r => r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = 1 } } });

        Choose(s, "Вася", "Silent Hill");

        ScenarioAssert.Accepted(s);
        Assert.Equal(chosen.Snapshot, s.Player("Вася").Offer!.Snapshot);
        Assert.NotEqual(s.Ruleset.Reward.DiceCount, s.Player("Вася").Offer!.Snapshot.DiceCount);
    }

    [Fact]
    public void Pending_choice_is_kept_when_the_choice_count_changes()
    {
        // The rules apply to the next roll; a choice already made by the server stays as it is
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var choice = s.Player("Вася").Choice;
        s.WithRuleset(ChoiceOf(1));

        Assert.Equal(choice, s.Player("Вася").Choice);
        Choose(s, "Вася", "Dead Space");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Ruleset_change_to_a_choice_of_three_applies_to_the_next_roll()
    {
        var s = Horror(1, seed: 42, s_horror);

        s.WithRuleset(ChoiceOf(3));
        s.Roll("Вася");

        Assert.Equal(3, ChoiceRoll(s).Offers.Count);
    }

    [Fact]
    public void Unknown_option_is_rejected()
    {
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var choiceId = s.Player("Вася").Choice!.ChoiceId;

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choiceId, "nope")), RejectionCodes.UnknownChoiceOption);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choiceId, "")), RejectionCodes.UnknownChoiceOption);
    }

    [Fact]
    public void Game_of_the_pool_that_is_not_an_option_is_rejected()
    {
        // Four games, three options: the fourth is a real game id but not on offer
        var s = Horror(s_horror);
        s.Roll("Вася");
        var choice = s.Player("Вася").Choice!;
        var notOffered = s_horror.Single(t => choice.Options.All(o => o.Id != OptionId(s, t)));

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choice.ChoiceId, OptionId(x, notOffered))), RejectionCodes.UnknownChoiceOption);
    }

    [Fact]
    public void Answer_to_a_choice_that_is_not_pending_is_rejected()
    {
        // Assumed code: a wrong ChoiceId means "there is no such pending choice" (NoPendingChoice), e.g. a stale tab
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new MakeChoice(x.PlayerId("Вася"), SequentialIds.Make(0x50000000, 9), OptionId(x, "Silent Hill"))),
            RejectionCodes.NoPendingChoice);
    }

    [Fact]
    public void Choosing_twice_from_two_tabs_is_rejected_the_second_time()
    {
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var choiceId = s.Player("Вася").Choice!.ChoiceId;
        Choose(s, "Вася", "Silent Hill");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choiceId, OptionId(x, "Alan Wake"))), RejectionCodes.NoPendingChoice);
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Вася").Offer!.GameId);
    }

    [Fact]
    public void Player_cannot_answer_another_players_choice()
    {
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var choiceId = s.Player("Вася").Choice!.ChoiceId;

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Петя"), choiceId, OptionId(x, "Silent Hill"))), RejectionCodes.NoPendingChoice);
    }

    [Fact]
    public void Roll_from_a_second_tab_while_choosing_is_rejected_and_keeps_the_choice()
    {
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var choice = s.Player("Вася").Choice;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.ChoicePending);
        Assert.Equal(choice, s.Player("Вася").Choice);
    }

    // ---- Kept on the server: a closed tab breaks nothing (T2) ----

    [Fact]
    public void Pending_choice_survives_replay_of_the_log_and_can_be_answered_after_it()
    {
        // Given Вася rolled a choice and closed the tab; the server restarts from the log
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var replayed = SeasonEngine.Replay(s.Log);

        // Then the replayed state holds the same choice
        Assert.Equal(s.State, replayed);
        Assert.Equal(s.Player("Вася").Choice, replayed.Players[s.PlayerId("Вася")].Choice);

        // And answering it on the replayed state gives the same result as on the live one
        var command = new MakeChoice(s.PlayerId("Вася"), s.Player("Вася").Choice!.ChoiceId, OptionId(s, "Dead Space"));
        var fromReplay = SeasonEngine.Execute(replayed, command, s.Context());
        Choose(s, "Вася", "Dead Space");
        Assert.True(fromReplay.IsAccepted, $"Rejected: {fromReplay.Rejection}");
        Assert.Equal(s.Last.Events, fromReplay.Events);
        Assert.Equal(s.State, fromReplay.State);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    // ---- Admin discard (D-89, D-91) ----

    [Fact]
    public void Admin_discard_drops_the_pending_choice_and_frees_its_games()
    {
        // Given Вася waits for a choice of all three horror games
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var choiceId = s.Player("Вася").Choice!.ChoiceId;

        // When the admin discards it
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Завис выбор", DiscardOffer: true));

        // Then the comment and a ChoiceDiscarded are logged, Вася is idle with nothing pending
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new PlayerAdjusted(s.PlayerId("Вася"), "Завис выбор"), new ChoiceDiscarded(s.PlayerId("Вася"), choiceId)],
            s.Last.Events);
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Idle, player.Phase);
        Assert.Null(player.Choice);
        Assert.Null(player.Offer);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        // And the games are free: Петя's roll has no misses and offers all three
        s.Roll("Петя");
        var rolled = ChoiceRoll(s);
        Assert.Empty(rolled.Misses);
        Assert.Equal(3, rolled.Offers.Count);

        // And the discarded choice cannot be answered any more
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new MakeChoice(x.PlayerId("Вася"), choiceId, OptionId(x, "Silent Hill"))), RejectionCodes.NoPendingChoice);
    }

    [Fact]
    public void Player_can_roll_again_after_the_admin_discarded_the_choice()
    {
        var s = Horror("Silent Hill", "Alan Wake", "Dead Space");
        s.Roll("Вася");
        var first = s.Player("Вася").Choice!.ChoiceId;
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сброс", DiscardOffer: true));

        s.Roll("Вася");

        var rolled = ChoiceRoll(s);
        Assert.NotEqual(first, rolled.ChoiceId);
        Assert.Equal(rolled.ChoiceId, s.Player("Вася").Choice!.ChoiceId);
    }

    // ---- Determinism (invariant 14) ----

    [Fact]
    public void Same_seed_gives_the_same_options()
    {
        foreach (var seed in s_seeds)
        {
            var first = Horror(3, seed, s_horror).Roll("Вася");
            var second = Horror(3, seed, s_horror).Roll("Вася");

            Assert.Equal(first.Log, second.Log);
            Assert.Equal(ChoiceRoll(first), ChoiceRoll(second));
        }
    }

    [Fact]
    public void Options_depend_on_the_random_source()
    {
        // Not a fixed pick: across seeds, 3 out of 4 games come out in different sets
        var sets = s_seeds
            .Select(seed => string.Join(",", ChoiceRoll(Horror(3, seed, s_horror).Roll("Вася")).Offers.Select(o => o.GameId).Order()))
            .Distinct()
            .Count();

        Assert.True(sets > 1, "Every seed gave the same options.");
    }
}
