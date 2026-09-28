using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// The admin corrects the hours of a completed run (W7; SPEC «Каждый кубик хранится отдельно. Если админ поправил часы
/// после броска, недостающие кубики докидываются, лишние снимаются с конца»; «Без часов … админ может поправить»;
/// D-13, D-14, D-96 (2), D-97). <see cref="CorrectRunHours"/> writes <see cref="RunHoursCorrected"/> (old and new hours,
/// the dice rolled and appended, the dice taken off the end with their values), then the difference:
/// <see cref="PointsChanged"/>, <see cref="PlayerMoved"/> from the current cell (forward along the arrows, back along the
/// walked path, never past the start) and <see cref="CoinsChanged"/> by the coin formula of the snapshot — each only
/// when it is not zero. The dice count uses the rule fixed in the run's snapshot. Challenge dice are never touched.
/// Test ruleset: hoursPerDie 3 (nearest), min 1, max 10; normal d4, hard d6; coins 1 per hour, min 3, ceiling 30 hours.
/// </summary>
public class HoursEditTests
{
    private const string Comment = "Часы по HLTB";

    /// <summary>Вася completes «Silent Hill» of <paramref name="hours"/> hours on <paramref name="difficulty"/> with the given dice.</summary>
    private static (Scenario S, Guid RunId) Completed(
        decimal? hours, int[] dice, Difficulty difficulty = Difficulty.Normal, Func<Ruleset, Ruleset>? ruleset = null, int? challengeDie = null)
    {
        var s = Scenario.New();
        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        if (challengeDie is not null)
        {
            s.WithRuleset(r => r with { Features = r.Features with { Challenges = true } });
        }

        s.WithCategory("Horror").WithGame("Silent Hill", hours, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithPlayers("Вася", "Петя");
        s.Roll("Вася").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        int[] script = challengeDie is { } c ? [.. dice, c] : dice;
        var estimate = s.State.Runs[runId].Snapshot.Hours is null;
        s.NextRandom(script);
        s.Complete(
            "Вася",
            difficulty,
            estimatedHours: estimate ? 6 : null,
            hoursSource: estimate ? "HLTB" : null,
            challengeDone: challengeDie is not null);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(5));
        return (s, runId);
    }

    private static Scenario Correct(Scenario s, Guid runId, decimal hours, string comment = Comment) =>
        s.Act(new CorrectRunHours(runId, hours, comment));

    private static EquatableArray<string> Cells(int from, int to) =>
        [.. (from <= to ? Enumerable.Range(from, to - from + 1) : Enumerable.Range(to, from - to + 1).Reverse()).Select(i => $"c{i}")];

    // ---- More hours: missing dice are rolled and appended ----

    [Fact]
    public void More_hours_roll_the_missing_dice_and_append_them()
    {
        // Given Вася completed a 6-hour game on normal: 2 dice showing 3 and 1 → 4 points, c4, 6 coins
        var (s, runId) = Completed(6, [3, 1]);
        var vasya = s.PlayerId("Вася");
        Assert.Equal((4, "c4", 6), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));

        // When the admin corrects the hours to 12 (4 dice): the two missing dice show 2 and 4 (a third value stays unused)
        s.NextRandom(2, 4, 1);
        Correct(s, runId, 12);

        // Then exactly: the correction with the appended dice, +6 points, 6 cells forward, +6 coins
        ScenarioAssert.Accepted(s);
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Equal(
            [
                new RunHoursCorrected(runId, vasya, 6, 12, [new Die(4, 2), new Die(4, 4)], [], Comment, s.Clock.UtcNow),
                new PointsChanged(vasya, 6, PointsReason.RunCorrection, runId),
                new PlayerMoved(vasya, "c4", "c10", 6, Cells(5, 10), MoveReason.RunCorrection, runId),
                new CoinsChanged(vasya, 6, CoinsReason.RunCorrection, runId),
            ],
            s.Last.Events);

        // And the run keeps the old dice first and the new ones at the end
        var run = s.State.Runs[runId];
        Assert.Equal(12, run.Hours);
        Assert.Equal([new Die(4, 3), new Die(4, 1), new Die(4, 2), new Die(4, 4)], run.Dice);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal((10, "c10", 12), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void Added_dice_follow_the_difficulty_of_the_run()
    {
        // A hard run throws d6: the missing dice are d6 too
        var (s, runId) = Completed(6, [6, 5], Difficulty.Hard);

        s.NextRandom(6, 6);
        Correct(s, runId, 12);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(6, 6), new Die(6, 6)], Assert.Single(s.LastEvents<RunHoursCorrected>()).Added);
        Assert.Equal([new Die(6, 6), new Die(6, 5), new Die(6, 6), new Die(6, 6)], s.State.Runs[runId].Dice);
        Assert.Equal(23, s.Player("Вася").Points);
    }

    // ---- Fewer hours: extra dice are taken off the end ----

    [Fact]
    public void Fewer_hours_take_the_extra_dice_off_the_end()
    {
        // Given a 12-hour game: dice 1, 2, 3, 4 → 10 points, c10, 12 coins
        var (s, runId) = Completed(12, [1, 2, 3, 4]);
        var vasya = s.PlayerId("Вася");
        s.NextRandom(1);

        // When the admin corrects the hours to 6 (2 dice)
        Correct(s, runId, 6);

        // Then the last two dice (3 and 4) go: -7 points, 7 cells back along the walked path, -6 coins; nothing is rolled
        ScenarioAssert.Accepted(s);
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Equal(
            [
                new RunHoursCorrected(runId, vasya, 12, 6, [], [new Die(4, 3), new Die(4, 4)], Comment, s.Clock.UtcNow),
                new PointsChanged(vasya, -7, PointsReason.RunCorrection, runId),
                new PlayerMoved(vasya, "c10", "c3", -7, Cells(9, 3), MoveReason.RunCorrection, runId),
                new CoinsChanged(vasya, -6, CoinsReason.RunCorrection, runId),
            ],
            s.Last.Events);
        Assert.Equal([new Die(4, 1), new Die(4, 2)], s.State.Runs[runId].Dice);
        Assert.Equal(6, s.State.Runs[runId].Hours);
        Assert.Equal((3, "c3", 6), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void Correcting_back_restores_the_original_dice()
    {
        var (s, runId) = Completed(6, [3, 1]);
        var before = s.Player("Вася");

        s.NextRandom(2, 4);
        Correct(s, runId, 12);
        Correct(s, runId, 6);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(4, 2), new Die(4, 4)], Assert.Single(s.LastEvents<RunHoursCorrected>()).Removed);
        Assert.Equal([new Die(4, 3), new Die(4, 1)], s.State.Runs[runId].Dice);
        Assert.Equal((before.Points, before.CellId, before.Coins), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void Fewer_hours_throw_no_dice()
    {
        // D-14: removing is deterministic; the random source is not asked at all
        var (s, runId) = Completed(12, [1, 2, 3, 4]);

        var result = SeasonEngine.Execute(s.State, new CorrectRunHours(runId, 3, Comment), s.Context() with { Random = new NoRandom() });

        Assert.True(result.IsAccepted, $"Rejected: {result.Rejection}");
        Assert.Equal([new Die(4, 2), new Die(4, 3), new Die(4, 4)], Assert.Single(result.Events.OfType<RunHoursCorrected>()).Removed);
    }

    [Fact]
    public void Removed_dice_keep_their_values_in_order_from_the_end()
    {
        // Given a 12-hour game: dice 2, 3, 4, 1
        var (s, runId) = Completed(12, [2, 3, 4, 1]);

        // When the hours drop to 6 (2 dice)
        Correct(s, runId, 6);

        // Then the event keeps exactly the dice taken off the end — 4 and 1 — in their order in the run
        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(4, 4), new Die(4, 1)], Assert.Single(s.LastEvents<RunHoursCorrected>()).Removed);
        Assert.Equal([new Die(4, 2), new Die(4, 3)], s.State.Runs[runId].Dice);
        Assert.Equal(-5, Assert.Single(s.LastEvents<PointsChanged>()).Delta);
    }

    // ---- The dice count by the snapshot (D-13, S1) ----

    [Fact]
    public void Dice_count_uses_the_rule_fixed_at_the_roll_not_the_current_one()
    {
        // Given the run was rolled with 3 hours per die; after completing, the admin changes it to 6 hours per die
        var (s, runId) = Completed(6, [3, 1]);
        s.WithRuleset(r => r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = 6 } } });

        // When the hours are corrected to 12
        s.NextRandom(2, 2);
        Correct(s, runId, 12);

        // Then the snapshot's rule counts 4 dice (the current one would give 2 — nothing to add)
        ScenarioAssert.Accepted(s);
        Assert.Equal(2, Assert.Single(s.LastEvents<RunHoursCorrected>()).Added.Count);
        Assert.Equal(4, s.State.Runs[runId].Dice.Count);
    }

    [Theory]
    [InlineData(6.0, 100.0, 8, 0)] // clamped at max 10 dice
    [InlineData(6.0, 1.0, 0, 1)] // 0.33 → 0 → min 1 die
    [InlineData(6.0, 7.5, 1, 0)] // 2.5 → nearest, half away from zero → 3
    [InlineData(6.0, 7.0, 0, 0)] // 2.33 → 2: the same count
    [InlineData(12.0, 4.4, 0, 3)] // 1.47 → 1
    public void Dice_count_follows_rounding_and_limits(double oldHours, double newHours, int added, int removed)
    {
        var (s, runId) = Completed((decimal)oldHours, [.. Enumerable.Repeat(1, (int)(oldHours / 3))]);

        Correct(s, runId, (decimal)newHours);

        ScenarioAssert.Accepted(s);
        var corrected = Assert.Single(s.LastEvents<RunHoursCorrected>());
        Assert.Equal((added, removed), (corrected.Added.Count, corrected.Removed.Count));
        Assert.All(corrected.Removed, d => Assert.Equal(new Die(4, 1), d));
        Assert.Equal((int)(oldHours / 3) + added - removed, s.State.Runs[runId].Dice.Count);
    }

    [Fact]
    public void Same_dice_count_changes_only_the_hours_and_the_coins()
    {
        // 6 → 7 hours: still 2 dice, but coins are ⌊7 × 1⌋ = 7 instead of 6
        var (s, runId) = Completed(6, [3, 1]);
        var vasya = s.PlayerId("Вася");

        Correct(s, runId, 7);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [
                new RunHoursCorrected(runId, vasya, 6, 7, [], [], Comment, s.Clock.UtcNow),
                new CoinsChanged(vasya, 1, CoinsReason.RunCorrection, runId),
            ],
            s.Last.Events);
        Assert.Equal((4, "c4", 7), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
        Assert.Equal(7, s.State.Runs[runId].Hours);
    }

    [Theory]
    [InlineData(30.0, 100.0)] // both above the ceiling of 10 dice × 3 hours: 10 dice and 30 coins
    [InlineData(3.0, 1.0)] // both at the minimum: 1 die and 3 coins
    public void Change_within_the_limits_writes_only_the_correction(double oldHours, double newHours)
    {
        var (s, runId) = Completed((decimal)oldHours, [.. Enumerable.Repeat(2, Math.Min(10, (int)(oldHours / 3)))]);
        var vasya = s.PlayerId("Вася");

        Correct(s, runId, (decimal)newHours);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [new RunHoursCorrected(runId, vasya, (decimal)oldHours, (decimal)newHours, [], [], Comment, s.Clock.UtcNow)],
            s.Last.Events);
    }

    // ---- Coins by the snapshot formula (D-96) ----

    [Fact]
    public void Coins_follow_the_formula_of_the_snapshot()
    {
        // Given the run was rolled with 2 coins per hour, min 5; the admin changed it to 1 per hour afterwards
        var (s, runId) = Completed(
            6, [1, 1], ruleset: r => r with { Reward = r.Reward with { Coins = new CoinReward { PerHour = 2, Min = 5 } } });
        Assert.Equal(12, s.Player("Вася").Coins);
        s.WithRuleset(r => r with { Reward = r.Reward with { Coins = new CoinReward { PerHour = 1, Min = 3 } } });

        // When the hours are corrected to 1.5 (1 die) and then to 100 (10 dice, ceiling 30 hours)
        Correct(s, runId, 1.5m);
        ScenarioAssert.Accepted(s);
        Assert.Equal(new CoinsChanged(s.PlayerId("Вася"), -7, CoinsReason.RunCorrection, runId), Assert.Single(s.LastEvents<CoinsChanged>()));

        Correct(s, runId, 100);
        ScenarioAssert.Accepted(s);
        Assert.Equal(55, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
        Assert.Equal(60, s.Player("Вася").Coins);
    }

    [Fact]
    public void Coins_can_be_taken_below_zero_by_a_correction()
    {
        // Вася spent his coins; the correction still takes back what the run gave too much
        var (s, runId) = Completed(12, [1, 1, 1, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Купил", CoinsDelta: -12));
        ScenarioAssert.Accepted(s);

        Correct(s, runId, 3);

        ScenarioAssert.Accepted(s);
        Assert.Equal(-9, Assert.Single(s.LastEvents<CoinsChanged>()).Delta);
        Assert.Equal(-9, s.Player("Вася").Coins);
    }

    // ---- Challenge dice stay (D-14, D-96) ----

    [Fact]
    public void Challenge_dice_are_not_touched()
    {
        // Given dice 3, 1 by hours and a challenge die 4
        var (s, runId) = Completed(6, [3, 1], challengeDie: 4);
        Assert.Equal(8, s.Player("Вася").Points);

        // When the hours go down to 3 (1 die): the last die by hours goes, not the challenge die
        Correct(s, runId, 3);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(4, 1)], Assert.Single(s.LastEvents<RunHoursCorrected>()).Removed);
        Assert.Equal(new PointsChanged(s.PlayerId("Вася"), -1, PointsReason.RunCorrection, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal([new Die(4, 3)], s.State.Runs[runId].Dice);
        Assert.Equal([new Die(4, 4)], s.State.Runs[runId].ChallengeDice);

        // And more hours append after the dice by hours; the challenge die stays apart
        s.NextRandom(2, 2, 2);
        Correct(s, runId, 12);
        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(4, 3), new Die(4, 2), new Die(4, 2), new Die(4, 2)], s.State.Runs[runId].Dice);
        Assert.Equal([new Die(4, 4)], s.State.Runs[runId].ChallengeDice);
        Assert.Equal(13, s.Player("Вася").Points);
    }

    // ---- An estimate can be corrected (SPEC «Без часов … админ может поправить») ----

    [Fact]
    public void Players_estimate_can_be_corrected()
    {
        // No hours in the pool: Вася estimated 6 hours
        var (s, runId) = Completed(null, [2, 2]);
        Assert.Equal(6, s.State.Runs[runId].Hours);

        s.NextRandom(4);
        Correct(s, runId, 9);

        ScenarioAssert.Accepted(s);
        var corrected = Assert.Single(s.LastEvents<RunHoursCorrected>());
        Assert.Equal((6m, 9m, 1), (corrected.OldHours, corrected.NewHours, corrected.Added.Count));
        Assert.Equal(9, s.State.Runs[runId].Hours);
        Assert.Equal(8, s.Player("Вася").Points);
    }

    // ---- The move: from the current cell (D-90, D-97) ----

    [Fact]
    public void Move_starts_from_the_current_cell_not_where_the_run_left_the_token()
    {
        // Given Вася completed run A (6 h: 3 + 1 → c4), then run B (6 h: 2 + 2 → c8)
        var (s, runA) = Completed(6, [3, 1]);
        s.Roll("Вася").Start("Вася").NextRandom(2, 2).Complete("Вася");
        ScenarioAssert.Accepted(s);
        Assert.Equal("c8", s.Player("Вася").CellId);

        // When run A gets 12 hours: +1 and +1
        s.NextRandom(1, 1);
        Correct(s, runA, 12);

        // Then the token goes from c8, not from c4
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(s.PlayerId("Вася"), "c8", "c10", 2, Cells(9, 10), MoveReason.RunCorrection, runA),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(10, s.Player("Вася").Points);
    }

    [Fact]
    public void Moving_back_retraces_the_walked_path()
    {
        var (s, runId) = Completed(12, [1, 2, 3, 4]);
        var pathBefore = s.Player("Вася").Path;

        Correct(s, runId, 9);

        ScenarioAssert.Accepted(s);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(Movement.Backward(s.State.Map, pathBefore, 4), moved.Path);
        Assert.Equal(pathBefore.After(moved), s.Player("Вася").Path);
        Assert.Equal("c6", s.Player("Вася").CellId);
    }

    [Fact]
    public void Moving_back_is_clamped_at_the_start()
    {
        // Given the admin moved Вася (10 points) to c2 after the run
        var (s, runId) = Completed(12, [1, 2, 3, 4]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "c2"));
        ScenarioAssert.Accepted(s);

        // When the hours drop to 6: -7 points, but only two cells back to the start
        Correct(s, runId, 6);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(s.PlayerId("Вася"), "c2", LinearMap.StartId, -7, ["c1", LinearMap.StartId], MoveReason.RunCorrection, runId),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal((3, LinearMap.StartId), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void On_the_start_fewer_hours_write_no_move()
    {
        // D-47: a move that enters no cell writes no event
        var (s, runId) = Completed(12, [1, 2, 3, 4]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: LinearMap.StartId));
        ScenarioAssert.Accepted(s);

        Correct(s, runId, 6);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunHoursCorrected), typeof(PointsChanged), typeof(CoinsChanged)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(LinearMap.StartId, s.Player("Вася").CellId);
    }

    [Fact]
    public void Correction_touches_only_the_owner_of_the_run()
    {
        var (s, runId) = Completed(6, [3, 1]);
        var petya = s.Player("Петя");

        s.NextRandom(4, 4);
        Correct(s, runId, 12);

        ScenarioAssert.Accepted(s);
        Assert.All(s.LastEvents<PointsChanged>(), e => Assert.Equal(s.PlayerId("Вася"), e.PlayerId));
        Assert.Equal(petya, s.Player("Петя"));
    }

    [Fact]
    public void Correction_leaves_the_owners_turn_alone()
    {
        // Вася is playing another game while the admin corrects his earlier run
        var (s, runId) = Completed(6, [3, 1]);
        s.Roll("Вася").Start("Вася");
        var active = s.Player("Вася").ActiveRunId;

        s.NextRandom(4, 4);
        Correct(s, runId, 12);

        ScenarioAssert.Accepted(s);
        Assert.Equal(active, s.Player("Вася").ActiveRunId);
        Assert.Equal(RunStatus.Playing, s.State.Runs[active!.Value].Status);
    }

    // ---- Refusals ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Hours_not_above_zero_are_rejected(double hours)
    {
        var (s, runId) = Completed(6, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Correct(x, runId, (decimal)hours), RejectionCodes.InvalidHours);
    }

    [Fact]
    public void Same_hours_are_rejected_as_nothing_to_change()
    {
        var (s, runId) = Completed(6, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Correct(x, runId, 6.0m), RejectionCodes.RunNothingToChange);
    }

    [Fact]
    public void Run_being_played_is_rejected()
    {
        var (s, _) = Completed(6, [3, 1]);
        s.Roll("Вася").Start("Вася");
        var playing = s.Player("Вася").ActiveRunId!.Value;

        ScenarioAssert.RejectsWithoutChanges(s, x => Correct(x, playing, 12), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Dropped_run_is_rejected()
    {
        var (s, _) = Completed(6, [3, 1]);
        s.Roll("Вася").Start("Вася");
        var dropped = s.Player("Вася").ActiveRunId!.Value;
        s.Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Correct(x, dropped, 12), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Unknown_run_is_rejected()
    {
        var (s, _) = Completed(6, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Correct(x, SequentialIds.Make(0x0BAD0000, 1), 12), RejectionCodes.RunUnknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_comment_is_rejected(string comment)
    {
        var (s, runId) = Completed(6, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Correct(x, runId, 12, comment), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Comment_over_the_limit_is_rejected()
    {
        var (s, runId) = Completed(6, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Correct(x, runId, 12, new string('я', Limits.MaxCommentLength + 1)), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Comment_at_the_limit_is_accepted()
    {
        var (s, runId) = Completed(6, [3, 1]);
        var comment = new string('я', Limits.MaxCommentLength);

        Correct(s, runId, 12, comment);

        ScenarioAssert.Accepted(s);
        Assert.Equal(comment, Assert.Single(s.LastEvents<RunHoursCorrected>()).Comment);
    }

    [Fact]
    public void Correction_is_allowed_while_the_season_is_closing()
    {
        // After the deadline proofs are still checked, so the hours can still be corrected
        var (s, runId) = Completed(6, [3, 1]);
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);

        s.NextRandom(4, 4);
        Correct(s, runId, 12);

        ScenarioAssert.Accepted(s);
        Assert.Equal(12, s.Player("Вася").Points);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Correction_after_the_season_is_finished_is_rejected(SeasonStatus status)
    {
        var (s, runId) = Completed(6, [3, 1]);
        s.MoveStatusToForcingFinish(status); // the run stays unchecked on purpose

        ScenarioAssert.RejectsWithoutChanges(s, x => Correct(x, runId, 12), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Rejected_correction_throws_no_dice()
    {
        var (s, runId) = Completed(6, [3, 1]);
        s.NextRandom(1, 1);

        ScenarioAssert.RejectsWithoutChanges(s, x => Correct(x, runId, 12, " "), RejectionCodes.CommentRequired);
        Assert.Equal(2, s.Random.ScriptedLeft);
    }

    // ---- The log ----

    [Fact]
    public void Replaying_the_log_gives_the_same_state_after_corrections()
    {
        var (s, runId) = Completed(6, [3, 1]);
        s.NextRandom(2, 4, 3);
        Correct(s, runId, 15);
        Correct(s, runId, 4);
        ScenarioAssert.Accepted(s);

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.State, replayed);
        Assert.Equal([new Die(4, 3)], replayed.Runs[runId].Dice);
        Assert.Equal(4, replayed.Runs[runId].Hours);
    }

    [Fact]
    public void Folding_the_correction_events_alone_updates_the_run_and_the_player()
    {
        var (s, runId) = Completed(6, [3, 1]);
        var before = s.State;

        s.NextRandom(2, 4);
        Correct(s, runId, 12);

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.State, s.Last.Events.Aggregate(before, SeasonEngine.Apply));
    }

    [Fact]
    public void Same_seed_gives_the_same_added_dice()
    {
        static Scenario Play()
        {
            var (s, runId) = Completed(6, [3, 1]);
            return Correct(s, runId, 30);
        }

        var first = Play();
        var second = Play();

        Assert.Equal(first.Log, second.Log);
        var added = Assert.Single(first.LastEvents<RunHoursCorrected>()).Added;
        Assert.Equal(8, added.Count);
        Assert.All(added, d =>
        {
            Assert.Equal(4, d.Sides);
            Assert.InRange(d.Value, 1, 4);
        });
    }
}

/// <summary>A random source that must not be asked: proves a command is deterministic without dice.</summary>
internal sealed class NoRandom : IRandomSource
{
    public int NextInt(int minInclusive, int maxExclusive) =>
        throw new InvalidOperationException("This command must not use randomness.");
}
