using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// Starting and completing a run: dice count by hours from the roll-time snapshot, die by difficulty,
/// each die stored separately, points and position grow by the same sum
/// (SPEC «Награда за прохождение», «Часы и кубы», «Снапшот»; D-04, D-12, D-13).
/// Default ruleset: hoursPerDie 3, nearest, min 1, max 10; easy d2, normal d4, hard d6, extreme d6.
/// </summary>
public class CompletionTests
{
    private static Scenario Playing(decimal? hours, Func<Ruleset, Ruleset>? ruleset = null)
    {
        var s = Scenario.New();
        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        s.WithCategory("Horror").WithGame("Silent Hill", hours, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);
        return s;
    }

    private static Func<Ruleset, Ruleset> DiceCount(Func<DiceCountRule, DiceCountRule> change) =>
        r => r with { Reward = r.Reward with { DiceCount = change(r.Reward.DiceCount) } };

    private static CompletionRolled DiceOf(Scenario s) => Assert.Single(s.LastEvents<CompletionRolled>());

    // ---- StartRun ----

    [Fact]
    public void Start_creates_playing_run_with_roll_snapshot()
    {
        // Given Вася rolled a game half an hour ago
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася");
        ScenarioAssert.Accepted(s);
        var offer = s.Player("Вася").Offer!;
        s.Advance(TimeSpan.FromMinutes(30));
        var startedAt = s.Clock.UtcNow;

        // When
        s.Start("Вася");

        // Then one RunStarted with the offered game and the snapshot from the roll
        ScenarioAssert.Accepted(s);
        var started = Assert.IsType<RunStarted>(Assert.Single(s.Last.Events));
        Assert.Equal(s.PlayerId("Вася"), started.PlayerId);
        Assert.Equal(s.GameId("Silent Hill"), started.GameId);
        Assert.Equal(offer.Snapshot, started.Snapshot);
        Assert.Equal(offer.RolledAt, started.RolledAt);
        Assert.Equal(startedAt, started.StartedAt);
        Assert.NotEqual(Guid.Empty, started.RunId);

        // And the player is Playing that run, the offer is consumed
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Playing, player.Phase);
        Assert.Equal(started.RunId, player.ActiveRunId);
        Assert.Null(player.Offer);
        Assert.Equal(
            new RunState(started.RunId, player.PlayerId, s.GameId("Silent Hill"), RunStatus.Playing, offer.Snapshot, offer.RolledAt, startedAt, null, null, []),
            s.State.Runs[started.RunId]);
    }

    [Fact]
    public void Start_when_idle_is_rejected_with_wrong_phase()
    {
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Start("Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Start_when_already_playing_is_rejected_with_wrong_phase()
    {
        var s = Playing(12);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Start("Вася"), RejectionCodes.WrongPhase);
        Assert.Single(s.State.Runs);
    }

    [Fact]
    public void Start_by_unknown_player_is_rejected()
    {
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new StartRun(SequentialIds.Make(0x0BAD0000, 1))), RejectionCodes.PlayerUnknown);
    }

    // ---- CompleteRun: the whole reward ----

    [Fact]
    public void Dice_sum_adds_points_and_moves_forward()
    {
        // Given Вася plays a 6-hour game: 6 / 3 = 2 dice, normal difficulty → d4
        var s = Playing(6);
        var runId = s.Player("Вася").ActiveRunId!.Value;
        var vasya = s.PlayerId("Вася");
        s.Advance(TimeSpan.FromHours(6));
        var completedAt = s.Clock.UtcNow;

        // When he completes and the dice show 3 and 1 (a third scripted value must stay unused)
        s.NextRandom(3, 1, 2).Complete("Вася", Difficulty.Normal);

        // Then the run is completed, each die stored separately, points and position grow by 4
        ScenarioAssert.Accepted(s);
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Equal(new RunCompleted(runId, vasya, Difficulty.Normal, 6m, completedAt), Assert.Single(s.LastEvents<RunCompleted>()));
        Assert.Equal(new CompletionRolled(runId, vasya, [new Die(4, 3), new Die(4, 1)]), DiceOf(s));
        Assert.Equal(new PointsChanged(vasya, 4, PointsReason.CompletionRoll, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(new PlayerMoved(vasya, "start", "c4", 4, ["c1", "c2", "c3", "c4"], MoveReason.CompletionRoll, runId), Assert.Single(s.LastEvents<PlayerMoved>()));

        var player = s.Player("Вася");
        Assert.Equal(4, player.Points);
        Assert.Equal("c4", player.CellId);
        Assert.Equal(TurnPhase.Idle, player.Phase);
        Assert.Null(player.ActiveRunId);
        Assert.Null(player.Offer);

        var run = s.State.Runs[runId];
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(Difficulty.Normal, run.Difficulty);
        Assert.Equal(6m, run.Hours);
        Assert.Equal([new Die(4, 3), new Die(4, 1)], run.Dice);
    }

    [Fact]
    public void Dice_are_rolled_before_the_token_moves()
    {
        var s = Playing(6);

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        var events = s.Last.Events.ToList();
        var dice = events.FindIndex(e => e is CompletionRolled);
        var moved = events.FindIndex(e => e is PlayerMoved);
        Assert.True(dice >= 0 && moved > dice, "CompletionRolled must precede PlayerMoved.");
    }

    [Fact]
    public void Points_and_position_accumulate_over_runs()
    {
        // Given two 3-hour games (1 die each, d4)
        var s = Scenario.New()
            .WithCategory("Short").WithGame("A", 3, "Short").WithGame("B", 3, "Short")
            .WithPlayers("Вася");

        s.Roll("Вася").Start("Вася").NextRandom(2).Complete("Вася");
        ScenarioAssert.Accepted(s);
        s.Roll("Вася").Start("Вася").NextRandom(3).Complete("Вася");
        ScenarioAssert.Accepted(s);

        Assert.Equal(5, s.Player("Вася").Points);
        Assert.Equal("c5", s.Player("Вася").CellId);
        Assert.Equal(new PlayerMoved(s.PlayerId("Вася"), "c2", "c5", 3, ["c3", "c4", "c5"], MoveReason.CompletionRoll, Assert.Single(s.LastEvents<RunCompleted>()).RunId), Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(2, s.State.Runs.Values.Count(r => r.Status == RunStatus.Completed));
    }

    // ---- Dice count (W1, D-13) ----

    [Theory]
    [InlineData(3.0, 1)] // 1.0
    [InlineData(4.0, 1)] // 1.33 → 1
    [InlineData(5.0, 2)] // 1.67 → 2
    [InlineData(6.0, 2)] // 2.0
    [InlineData(7.5, 3)] // 2.5 → 3: half rounds up (banker's rounding would give 2)
    [InlineData(13.5, 5)] // 4.5 → 5 (banker's: 4)
    [InlineData(0.5, 1)] // 0.17 → 0 → min 1
    [InlineData(1.5, 1)] // 0.5 → 1
    [InlineData(28.5, 10)] // 9.5 → 10
    [InlineData(31.5, 10)] // 10.5 → 11 → max 10
    [InlineData(100.0, 10)] // 33 → max 10
    public void Dice_count_is_hours_per_die_rounded_nearest_within_limits(double hours, int expectedDice)
    {
        var s = Playing((decimal)hours);

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(expectedDice, DiceOf(s).Dice.Count);
        Assert.Equal(expectedDice, s.State.Runs.Values.Single().Dice.Count);
    }

    [Theory]
    [InlineData(Rounding.Floor, 7.5, 2)]
    [InlineData(Rounding.Floor, 2.0, 1)] // 0 → min 1
    [InlineData(Rounding.Ceil, 4.0, 2)]
    [InlineData(Rounding.Ceil, 6.0, 2)]
    public void Dice_count_uses_configured_rounding(Rounding rounding, double hours, int expectedDice)
    {
        var s = Playing((decimal)hours, DiceCount(d => d with { Rounding = rounding }));

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(expectedDice, DiceOf(s).Dice.Count);
    }

    [Theory]
    [InlineData(1.0, 2)] // 0.33 → 0 → min 2
    [InlineData(9.0, 3)] // 3
    [InlineData(30.0, 3)] // 10 → max 3
    public void Dice_count_respects_configured_min_and_max(double hours, int expectedDice)
    {
        var s = Playing((decimal)hours, DiceCount(d => d with { Min = 2, Max = 3 }));

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(expectedDice, DiceOf(s).Dice.Count);
    }

    // ---- Die by difficulty (W2) ----

    [Theory]
    [InlineData(Difficulty.Easy, 2)]
    [InlineData(Difficulty.Normal, 4)]
    [InlineData(Difficulty.Hard, 6)]
    [InlineData(Difficulty.Extreme, 6)]
    public void Die_sides_follow_difficulty(Difficulty difficulty, int sides)
    {
        // 12 hours → 4 dice
        var s = Playing(12);

        s.Complete("Вася", difficulty);

        ScenarioAssert.Accepted(s);
        var dice = DiceOf(s).Dice;
        Assert.Equal(4, dice.Count);
        Assert.All(dice, d =>
        {
            Assert.Equal(sides, d.Sides);
            Assert.InRange(d.Value, 1, sides);
        });
        Assert.Equal(difficulty, Assert.Single(s.LastEvents<RunCompleted>()).Difficulty);
        Assert.Equal(dice.Sum(d => d.Value), s.Player("Вася").Points);
    }

    [Fact]
    public void Die_value_comes_from_the_random_source_in_die_range()
    {
        // Easy is d2, requested as NextInt(1, 3): the scripted 2 is the face of the only die (3 hours → 1 die).
        var s = Playing(3);

        s.NextRandom(2).Complete("Вася", Difficulty.Easy);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(2, 2)], DiceOf(s).Dice);
        Assert.Equal(2, s.Player("Вася").Points);
    }

    // ---- Snapshot (S1, S2, G4) ----

    [Fact]
    public void Ruleset_change_after_roll_does_not_change_dice_count()
    {
        // Given Вася rolled a 6-hour game under hoursPerDie = 3 (2 dice)
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася");
        ScenarioAssert.Accepted(s);

        // When the admin changes hoursPerDie to 1 before he starts and completes
        s.WithRuleset(DiceCount(d => d with { HoursPerDie = 1 }));
        s.Start("Вася").Complete("Вася");

        // Then the roll-time rule still applies
        ScenarioAssert.Accepted(s);
        Assert.Equal(2, DiceOf(s).Dice.Count);
    }

    [Fact]
    public void Ruleset_change_while_playing_does_not_change_dice_count_or_sides()
    {
        var s = Playing(6);

        s.WithRuleset(r => r with
        {
            Reward = r.Reward with
            {
                DiceCount = r.Reward.DiceCount with { HoursPerDie = 1 },
                DieByDifficulty = r.Reward.DieByDifficulty with { Normal = new DieRule { Sides = 20 } },
            },
        });
        s.Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        var dice = DiceOf(s).Dice;
        Assert.Equal(2, dice.Count);
        Assert.All(dice, d => Assert.Equal(4, d.Sides));
    }

    [Fact]
    public void Ruleset_change_before_roll_applies_to_that_run()
    {
        var s = Playing(6, r => r with
        {
            Reward = r.Reward with
            {
                DiceCount = r.Reward.DiceCount with { HoursPerDie = 1 },
                DieByDifficulty = r.Reward.DieByDifficulty with { Normal = new DieRule { Sides = 20 } },
            },
        });

        s.Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        var dice = DiceOf(s).Dice;
        Assert.Equal(6, dice.Count);
        Assert.All(dice, d => Assert.Equal(20, d.Sides));
    }

    [Fact]
    public void Pool_hours_change_after_roll_does_not_change_the_run()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася");
        ScenarioAssert.Accepted(s);

        s.ChangePoolHours("Silent Hill", 30);
        s.Start("Вася").Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(6m, Assert.Single(s.LastEvents<RunCompleted>()).Hours);
        Assert.Equal(2, DiceOf(s).Dice.Count);
    }

    // ---- Hours required (W6) ----

    [Fact]
    public void Completion_without_hours_is_rejected_with_hours_required()
    {
        var s = Playing(null);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Вася"), RejectionCodes.HoursRequired);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
    }

    [Fact]
    public void Completion_without_hours_uses_player_estimate()
    {
        var s = Playing(null);

        s.Complete("Вася", Difficulty.Normal, estimatedHours: 6, hoursSource: "https://howlongtobeat.com/game/1");

        ScenarioAssert.Accepted(s);
        Assert.Equal(6m, Assert.Single(s.LastEvents<RunCompleted>()).Hours);
        Assert.Equal(2, DiceOf(s).Dice.Count);
        Assert.Equal(6m, s.State.Runs.Values.Single().Hours);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    public void Completion_with_non_positive_estimate_is_rejected_with_invalid_hours(double estimate)
    {
        var s = Playing(null);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Complete("Вася", Difficulty.Normal, estimatedHours: (decimal)estimate, hoursSource: "HLTB"), RejectionCodes.InvalidHours);
    }

    [Fact]
    public void Rejected_completion_can_be_retried_with_an_estimate()
    {
        var s = Playing(null);
        s.ExpectRejection().Complete("Вася");
        Assert.False(s.Last.IsAccepted);

        s.Complete("Вася", Difficulty.Hard, estimatedHours: 3, hoursSource: "HLTB");

        ScenarioAssert.Accepted(s);
        Assert.Equal(new[] { 6 }, DiceOf(s).Dice.Select(d => d.Sides));
    }

    // ---- Phase and player checks ----

    [Fact]
    public void Complete_when_idle_is_rejected_with_wrong_phase()
    {
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Complete_when_rolling_is_rejected_with_wrong_phase()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Completing_twice_is_rejected_with_wrong_phase()
    {
        var s = Playing(6).Complete("Вася");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Complete_by_unknown_player_is_rejected()
    {
        var s = Playing(6);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new CompleteRun(SequentialIds.Make(0x0BAD0000, 1), Difficulty.Normal)), RejectionCodes.PlayerUnknown);
    }

    [Fact]
    public void Completing_one_player_does_not_touch_another()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithPlayers("Вася", "Петя")
            .Roll("Вася").Start("Вася")
            .Roll("Петя").Start("Петя");
        ScenarioAssert.Accepted(s);
        var petya = s.Player("Петя");

        s.Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(petya, s.Player("Петя"));
        Assert.All(s.Last.Events.OfType<PointsChanged>(), e => Assert.Equal(s.PlayerId("Вася"), e.PlayerId));
        Assert.All(s.Last.Events.OfType<PlayerMoved>(), e => Assert.Equal(s.PlayerId("Вася"), e.PlayerId));
    }
}
