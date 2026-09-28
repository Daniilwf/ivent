using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// The admin changes the difficulty of a completed run by the proof (W8; SPEC «Сложность засчитывается по пруфу. Если
/// игрок понизил сложность посреди игры, считается более низкая»; Q-5, D-97). Every die — the challenge dice too —
/// becomes ⌈old × new sides / old sides⌉; <see cref="RunDifficultyChanged"/> keeps both values of each die. Points and
/// position change by the difference (no coins: they do not depend on difficulty). No randomness. The difficulty's event:
/// any pending difficulty event of the run is resolved «не применимо» with the comment, the player and the run
/// (<see cref="ManualEffectResolved"/>) and leaves the pending list; a new difficulty with <c>grantEvent</c> creates one.
/// Test ruleset: easy d2, normal d4, hard d6, extreme d6 with a good event; hoursPerDie 3.
/// </summary>
public class DifficultyChangeTests
{
    private const string Comment = "По пруфу — нормальная";

    private static (Scenario S, Guid RunId) Completed(
        Difficulty difficulty, int[] dice, decimal hours = 6, Func<Ruleset, Ruleset>? ruleset = null, int[]? challengeDice = null)
    {
        var s = Scenario.New();
        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        if (challengeDice is not null)
        {
            s.WithRuleset(r => r with
            {
                Features = r.Features with { Challenges = true },
                Reward = r.Reward with { ChallengeBonus = new ChallengeBonus { ExtraDice = challengeDice.Length } },
            });
        }

        s.WithCategory("Horror").WithGame("Silent Hill", hours, "Horror").WithGame("Alan Wake", hours, "Horror").WithGame("Dead Space", hours, "Horror")
            .WithPlayers("Вася", "Петя");
        s.Roll("Вася").Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom([.. dice, .. challengeDice ?? []]);
        s.Complete("Вася", difficulty, challengeDone: challengeDice is not null);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(3));
        return (s, runId);
    }

    private static Scenario Change(Scenario s, Guid runId, Difficulty difficulty, string comment = Comment) =>
        s.Act(new ChangeRunDifficulty(runId, difficulty, comment));

    private static Guid PendingEffectOf(Scenario s, Guid runId) =>
        Assert.Single(s.State.ManualEffects.Values, e => e.RunId == runId && e.Source == ManualEffectSource.Difficulty).EffectId;

    // ---- Lower: hard → normal ----

    [Fact]
    public void Lowering_recalculates_every_die_and_takes_the_difference()
    {
        // Given Вася completed a 6-hour game on hard: d6 showing 5 and 2 → 7 points, c7
        var (s, runId) = Completed(Difficulty.Hard, [5, 2]);
        var vasya = s.PlayerId("Вася");
        var coins = s.Player("Вася").Coins;

        // When the admin sets normal by the proof
        Change(s, runId, Difficulty.Normal);

        // Then 5 → ⌈5 × 4 / 6⌉ = 4, 2 → ⌈2 × 4 / 6⌉ = 2: one point and one cell back; coins stay
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [
                new RunDifficultyChanged(
                    runId,
                    vasya,
                    Difficulty.Hard,
                    Difficulty.Normal,
                    [new DieChange(new Die(6, 5), new Die(4, 4)), new DieChange(new Die(6, 2), new Die(4, 2))],
                    [],
                    Comment,
                    s.Clock.UtcNow),
                new PointsChanged(vasya, -1, PointsReason.RunCorrection, runId),
                new PlayerMoved(vasya, "c7", "c6", -1, ["c6"], MoveReason.RunCorrection, runId),
            ],
            s.Last.Events);

        var run = s.State.Runs[runId];
        Assert.Equal(Difficulty.Normal, run.Difficulty);
        Assert.Equal([new Die(4, 4), new Die(4, 2)], run.Dice);
        Assert.Equal((6, "c6", coins), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    // ---- Higher: normal → hard ----

    [Fact]
    public void Raising_recalculates_every_die_and_adds_the_difference()
    {
        // normal d4 showing 3 and 1 → 4 points, c4; hard: 3 → ⌈18 / 4⌉ = 5, 1 → ⌈6 / 4⌉ = 2
        var (s, runId) = Completed(Difficulty.Normal, [3, 1]);
        var vasya = s.PlayerId("Вася");

        Change(s, runId, Difficulty.Hard, "По пруфу — сложная");

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [
                new RunDifficultyChanged(
                    runId,
                    vasya,
                    Difficulty.Normal,
                    Difficulty.Hard,
                    [new DieChange(new Die(4, 3), new Die(6, 5)), new DieChange(new Die(4, 1), new Die(6, 2))],
                    [],
                    "По пруфу — сложная",
                    s.Clock.UtcNow),
                new PointsChanged(vasya, 3, PointsReason.RunCorrection, runId),
                new PlayerMoved(vasya, "c4", "c7", 3, ["c5", "c6", "c7"], MoveReason.RunCorrection, runId),
            ],
            s.Last.Events);
        Assert.Equal([new Die(6, 5), new Die(6, 2)], s.State.Runs[runId].Dice);
        Assert.Equal((7, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    // ---- The formula for each pair (Q-5) ----

    [Theory]
    [InlineData(Difficulty.Hard, Difficulty.Normal, 5, 4)]
    [InlineData(Difficulty.Hard, Difficulty.Normal, 6, 4)]
    [InlineData(Difficulty.Hard, Difficulty.Normal, 3, 2)]
    [InlineData(Difficulty.Hard, Difficulty.Normal, 1, 1)]
    [InlineData(Difficulty.Normal, Difficulty.Hard, 3, 5)]
    [InlineData(Difficulty.Normal, Difficulty.Hard, 4, 6)]
    [InlineData(Difficulty.Normal, Difficulty.Hard, 2, 3)]
    [InlineData(Difficulty.Normal, Difficulty.Hard, 1, 2)]
    [InlineData(Difficulty.Normal, Difficulty.Easy, 1, 1)]
    [InlineData(Difficulty.Normal, Difficulty.Easy, 2, 1)]
    [InlineData(Difficulty.Normal, Difficulty.Easy, 3, 2)]
    [InlineData(Difficulty.Normal, Difficulty.Easy, 4, 2)]
    [InlineData(Difficulty.Easy, Difficulty.Normal, 1, 2)]
    [InlineData(Difficulty.Easy, Difficulty.Normal, 2, 4)]
    [InlineData(Difficulty.Easy, Difficulty.Hard, 1, 3)]
    [InlineData(Difficulty.Easy, Difficulty.Extreme, 2, 6)]
    [InlineData(Difficulty.Hard, Difficulty.Easy, 1, 1)]
    [InlineData(Difficulty.Hard, Difficulty.Easy, 3, 1)]
    [InlineData(Difficulty.Hard, Difficulty.Easy, 4, 2)]
    [InlineData(Difficulty.Extreme, Difficulty.Easy, 6, 2)]
    [InlineData(Difficulty.Hard, Difficulty.Extreme, 5, 5)]
    [InlineData(Difficulty.Extreme, Difficulty.Hard, 1, 1)]
    public void Each_die_becomes_the_ceiling_of_old_times_new_sides_over_old_sides(Difficulty from, Difficulty to, int before, int after)
    {
        // A 3-hour game: one die
        var (s, runId) = Completed(from, [before], hours: 3);
        var sides = new Dictionary<Difficulty, int> { [Difficulty.Easy] = 2, [Difficulty.Normal] = 4, [Difficulty.Hard] = 6, [Difficulty.Extreme] = 6 };
        var points = s.Player("Вася").Points;

        Change(s, runId, to);

        ScenarioAssert.Accepted(s);
        var changed = Assert.Single(s.LastEvents<RunDifficultyChanged>());
        Assert.Equal([new DieChange(new Die(sides[from], before), new Die(sides[to], after))], changed.Dice);
        Assert.Equal((from, to), (changed.OldDifficulty, changed.NewDifficulty));
        Assert.Equal(
            after == before ? [] : [new PointsChanged(s.PlayerId("Вася"), after - before, PointsReason.RunCorrection, runId)],
            s.LastEvents<PointsChanged>());
        Assert.Equal(points + after - before, s.Player("Вася").Points);
        Assert.Equal([new Die(sides[to], after)], s.State.Runs[runId].Dice);
    }

    [Fact]
    public void Same_sides_change_no_dice_points_or_position()
    {
        // hard and extreme are both d6 in the test ruleset; hard → extreme only adds the good event
        var (s, runId) = Completed(Difficulty.Hard, [5, 2]);
        var before = s.Player("Вася");

        Change(s, runId, Difficulty.Extreme, "Выше сложной по пруфу");

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunDifficultyChanged), typeof(ManualEffectCreated)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(
            [new DieChange(new Die(6, 5), new Die(6, 5)), new DieChange(new Die(6, 2), new Die(6, 2))],
            Assert.Single(s.LastEvents<RunDifficultyChanged>()).Dice);
        Assert.Equal((before.Points, before.CellId, before.Coins), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    // ---- Challenge dice are recalculated too (D-96 (2)) ----

    [Fact]
    public void Challenge_dice_are_recalculated_too()
    {
        // normal: dice 3, 1 and a challenge die 4 → 8 points; hard: 5, 2 and ⌈4 × 6 / 4⌉ = 6 → 13
        var (s, runId) = Completed(Difficulty.Normal, [3, 1], challengeDice: [4]);
        Assert.Equal(8, s.Player("Вася").Points);

        Change(s, runId, Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        var changed = Assert.Single(s.LastEvents<RunDifficultyChanged>());
        Assert.Equal([new DieChange(new Die(4, 4), new Die(6, 6))], changed.ChallengeDice);
        Assert.Equal(new PointsChanged(s.PlayerId("Вася"), 5, PointsReason.RunCorrection, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal([new Die(6, 6)], s.State.Runs[runId].ChallengeDice);
        Assert.Equal([new Die(6, 5), new Die(6, 2)], s.State.Runs[runId].Dice);
        Assert.Equal(13, s.Player("Вася").Points);
    }

    // ---- Die rules from the snapshot (S1) ----

    [Fact]
    public void Die_sides_come_from_the_roll_snapshot()
    {
        // Given the run was rolled with normal d4; afterwards the admin changed normal to d10
        var (s, runId) = Completed(Difficulty.Hard, [6, 3]);
        s.WithRuleset(r => r with
        {
            Reward = r.Reward with { DieByDifficulty = r.Reward.DieByDifficulty with { Normal = new DieRule { Sides = 10 } } },
        });

        Change(s, runId, Difficulty.Normal);

        // Then the snapshot's d4 is used: 6 → 4, 3 → 2
        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(4, 4), new Die(4, 2)], s.State.Runs[runId].Dice);
    }

    [Fact]
    public void Granted_event_comes_from_the_roll_snapshot()
    {
        // Given extreme granted a good event at the roll; afterwards the admin removed it from the rules
        var (s, runId) = Completed(Difficulty.Hard, [6, 3]);
        s.WithRuleset(r => r with
        {
            Reward = r.Reward with { DieByDifficulty = r.Reward.DieByDifficulty with { Extreme = new DieRule { Sides = 6 } } },
        });

        Change(s, runId, Difficulty.Extreme);

        ScenarioAssert.Accepted(s);
        Assert.Equal(EventKind.Good, Assert.Single(s.LastEvents<ManualEffectCreated>()).DrawEvent);
    }

    // ---- The difficulty's event (Q-5, D-97) ----

    [Fact]
    public void Lowering_from_extreme_marks_the_pending_good_event_not_applicable()
    {
        // Given Вася completed on «выше сложной»: d6 showing 5 and 2, and a good event waits
        var (s, runId) = Completed(Difficulty.Extreme, [5, 2]);
        var vasya = s.PlayerId("Вася");
        var effectId = PendingEffectOf(s, runId);

        // When the proof shows normal
        Change(s, runId, Difficulty.Normal);

        // Then the dice and points follow, and the effect is resolved «не применимо» with the admin's comment
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunDifficultyChanged), typeof(PointsChanged), typeof(PlayerMoved), typeof(ManualEffectResolved)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new ManualEffectResolved(effectId, vasya, runId, ManualEffectOutcome.NotApplicable, Comment), s.Last.Events[^1]);
        Assert.Equal(new PointsChanged(vasya, -1, PointsReason.RunCorrection, runId), s.Last.Events[1]);
        Assert.Empty(s.State.ManualEffects);
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
    }

    [Fact]
    public void Lowering_from_extreme_with_the_same_dice_writes_only_the_change_and_the_resolution()
    {
        var (s, runId) = Completed(Difficulty.Extreme, [5, 2]);
        var effectId = PendingEffectOf(s, runId);

        Change(s, runId, Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunDifficultyChanged), typeof(ManualEffectResolved)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new ManualEffectResolved(effectId, s.PlayerId("Вася"), runId, ManualEffectOutcome.NotApplicable, Comment), s.Last.Events[1]);
        Assert.False(s.State.ManualEffects.ContainsKey(effectId));
    }

    [Fact]
    public void Only_the_event_of_this_run_is_resolved()
    {
        // Given two runs on «выше сложной» and a bad event from a drop, all pending
        var (s, runA) = Completed(Difficulty.Extreme, [5, 2]);
        s.Roll("Вася").Start("Вася").Complete("Вася", Difficulty.Extreme);
        ScenarioAssert.Accepted(s);
        var runB = s.Log.OfType<RunCompleted>().Last().RunId;
        s.Roll("Петя").Start("Петя");
        s.Act(new DropRun(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        var effectA = PendingEffectOf(s, runA);
        var others = s.State.ManualEffects.Keys.Where(k => k != effectA).ToList();
        Assert.Equal(2, others.Count);

        // When run A is lowered
        Change(s, runA, Difficulty.Hard);

        // Then only its event is resolved; run B's and the drop's stay pending
        ScenarioAssert.Accepted(s);
        Assert.Equal(effectA, Assert.Single(s.LastEvents<ManualEffectResolved>()).EffectId);
        Assert.Equal(others.Order(), s.State.ManualEffects.Keys.Order());
        Assert.Equal(runB, s.State.ManualEffects.Values.Single(e => e.Source == ManualEffectSource.Difficulty).RunId);
    }

    [Fact]
    public void Raising_to_extreme_creates_a_good_event()
    {
        // normal: 3 and 1 → extreme d6: 5 and 2
        var (s, runId) = Completed(Difficulty.Normal, [3, 1]);
        var vasya = s.PlayerId("Вася");

        Change(s, runId, Difficulty.Extreme, "Выше сложной по пруфу");

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunDifficultyChanged), typeof(PointsChanged), typeof(PlayerMoved), typeof(ManualEffectCreated)],
            s.Last.Events.Select(e => e.GetType()));
        var created = Assert.IsType<ManualEffectCreated>(s.Last.Events[^1]);
        Assert.NotEqual(Guid.Empty, created.EffectId);
        Assert.Equal(new ManualEffectCreated(created.EffectId, vasya, EventKind.Good, ManualEffectSource.Difficulty, runId), created);
        Assert.Equal(
            new PendingManualEffect(created.EffectId, vasya, EventKind.Good, ManualEffectSource.Difficulty, runId),
            Assert.Single(s.State.ManualEffects.Values));
    }

    [Fact]
    public void Lowering_again_resolves_the_event_created_by_the_raise()
    {
        // extreme → hard resolves the first event, hard → extreme creates a second, extreme → normal resolves that one
        var (s, runId) = Completed(Difficulty.Extreme, [4, 4]);
        var first = PendingEffectOf(s, runId);
        Change(s, runId, Difficulty.Hard);
        Change(s, runId, Difficulty.Extreme);
        var second = PendingEffectOf(s, runId);
        Assert.NotEqual(first, second);

        Change(s, runId, Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Equal(second, Assert.Single(s.LastEvents<ManualEffectResolved>()).EffectId);
        Assert.Empty(s.State.ManualEffects);
        Assert.Equal(2, s.Log.OfType<ManualEffectResolved>().Count());
    }

    [Fact]
    public void Lowering_between_difficulties_without_events_resolves_nothing()
    {
        var (s, runId) = Completed(Difficulty.Extreme, [4, 4]);
        Change(s, runId, Difficulty.Hard);

        Change(s, runId, Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ManualEffectResolved>());
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
    }

    [Fact]
    public void Moving_between_two_granting_difficulties_resolves_the_old_event_and_creates_the_new()
    {
        // Given the rules: hard grants a bad event, extreme a good one
        var (s, runId) = Completed(
            Difficulty.Extreme,
            [4, 4],
            ruleset: r => r with
            {
                Reward = r.Reward with
                {
                    DieByDifficulty = r.Reward.DieByDifficulty with { Hard = new DieRule { Sides = 6, GrantEvent = EventKind.Bad } },
                },
            });
        var good = PendingEffectOf(s, runId);

        Change(s, runId, Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new ManualEffectResolved(good, s.PlayerId("Вася"), runId, ManualEffectOutcome.NotApplicable, Comment),
            Assert.Single(s.LastEvents<ManualEffectResolved>()));
        var created = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal((EventKind.Bad, ManualEffectSource.Difficulty, (Guid?)runId), (created.DrawEvent, created.Source, created.RunId));
        Assert.Equal(created.EffectId, Assert.Single(s.State.ManualEffects.Keys));
    }

    [Fact]
    public void Resolved_event_carries_the_player_and_the_run()
    {
        // Петя's run is lowered: the resolution names Петя and his run, not whoever else has effects
        var (s, _) = Completed(Difficulty.Extreme, [5, 2]);
        s.Roll("Петя").Start("Петя");
        var petyaRun = s.Player("Петя").ActiveRunId!.Value;
        s.NextRandom(4, 4).Complete("Петя", Difficulty.Extreme);
        ScenarioAssert.Accepted(s);
        var effectId = PendingEffectOf(s, petyaRun);

        Change(s, petyaRun, Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        var resolved = Assert.Single(s.LastEvents<ManualEffectResolved>());
        Assert.Equal(
            new ManualEffectResolved(effectId, s.PlayerId("Петя"), petyaRun, ManualEffectOutcome.NotApplicable, Comment),
            resolved);
    }

    /// <summary>The good event of «выше сложной» was already played (resolved «применено») before the proof check.</summary>
    private static SeasonState AppliedGoodEvent(Scenario s, Guid runId) =>
        SeasonEngine.Apply(
            s.State,
            new ManualEffectResolved(PendingEffectOf(s, runId), s.PlayerId("Вася"), runId, ManualEffectOutcome.Applied, "Разыграли"));

    [Fact]
    public void Lowering_after_the_good_event_was_applied_leaves_it_to_the_admin()
    {
        // Given the good event of «выше сложной» was already played
        var (s, runId) = Completed(Difficulty.Extreme, [5, 2]);
        var applied = AppliedGoodEvent(s, runId);
        Assert.Empty(applied.ManualEffects);

        // When the proof shows normal
        var result = SeasonEngine.Execute(applied, new ChangeRunDifficulty(runId, Difficulty.Normal, Comment), s.Context());

        // Then the dice and points follow; nothing is resolved (it is gone already) and nothing fails — the admin fixes it by hand
        Assert.True(result.IsAccepted, $"Rejected: {result.Rejection}");
        Assert.Equal(
            [typeof(RunDifficultyChanged), typeof(PointsChanged), typeof(PlayerMoved)],
            result.Events.Select(e => e.GetType()));
        Assert.Empty(result.State.ManualEffects);
        Assert.Equal(Difficulty.Normal, result.State.Runs[runId].Difficulty);
    }

    [Fact]
    public void Applied_then_lowered_then_raised_gives_a_second_good_event()
    {
        // D-97: accepted — the admin corrects the second event by hand
        var (s, runId) = Completed(Difficulty.Extreme, [5, 2]);
        var first = PendingEffectOf(s, runId);
        var state = AppliedGoodEvent(s, runId);

        var lowered = SeasonEngine.Execute(state, new ChangeRunDifficulty(runId, Difficulty.Normal, Comment), s.Context());
        Assert.True(lowered.IsAccepted, $"Rejected: {lowered.Rejection}");
        var raised = SeasonEngine.Execute(lowered.State, new ChangeRunDifficulty(runId, Difficulty.Extreme, "Всё-таки выше сложной"), s.Context());

        Assert.True(raised.IsAccepted, $"Rejected: {raised.Rejection}");
        var created = Assert.Single(raised.Events.OfType<ManualEffectCreated>());
        Assert.NotEqual(first, created.EffectId);
        Assert.Equal(
            new PendingManualEffect(created.EffectId, s.PlayerId("Вася"), EventKind.Good, ManualEffectSource.Difficulty, runId),
            Assert.Single(raised.State.ManualEffects.Values));
    }

    // ---- Determinism: no dice are thrown ----

    [Fact]
    public void Difficulty_change_uses_no_randomness()
    {
        var (s, runId) = Completed(Difficulty.Extreme, [5, 2], challengeDice: [3]);

        var result = SeasonEngine.Execute(
            s.State, new ChangeRunDifficulty(runId, Difficulty.Easy, Comment), s.Context() with { Random = new NoRandom() });

        Assert.True(result.IsAccepted, $"Rejected: {result.Rejection}");
        var changed = Assert.Single(result.Events.OfType<RunDifficultyChanged>());
        Assert.Equal([new Die(2, 2), new Die(2, 1)], changed.Dice.Select(d => d.After));
        Assert.Equal([new Die(2, 1)], changed.ChallengeDice.Select(d => d.After));
    }

    // ---- Refusals ----

    [Fact]
    public void Same_difficulty_is_rejected_as_nothing_to_change()
    {
        var (s, runId) = Completed(Difficulty.Normal, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Change(x, runId, Difficulty.Normal), RejectionCodes.RunNothingToChange);
    }

    [Fact]
    public void Run_being_played_is_rejected()
    {
        var (s, _) = Completed(Difficulty.Normal, [3, 1]);
        s.Roll("Вася").Start("Вася");
        var playing = s.Player("Вася").ActiveRunId!.Value;

        ScenarioAssert.RejectsWithoutChanges(s, x => Change(x, playing, Difficulty.Hard), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Dropped_run_is_rejected()
    {
        var (s, _) = Completed(Difficulty.Normal, [3, 1]);
        s.Roll("Вася").Start("Вася");
        var dropped = s.Player("Вася").ActiveRunId!.Value;
        s.Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Change(x, dropped, Difficulty.Hard), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Tech_rerolled_run_is_rejected()
    {
        var (s, _) = Completed(Difficulty.Normal, [3, 1]);
        s.Roll("Вася").Start("Вася");
        var rerolled = s.Player("Вася").ActiveRunId!.Value;
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, "Не запускается"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.TechRerolled, s.State.Runs[rerolled].Status);

        ScenarioAssert.RejectsWithoutChanges(s, x => Change(x, rerolled, Difficulty.Hard), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Undefined_difficulty_is_rejected_as_an_invalid_command()
    {
        var (s, runId) = Completed(Difficulty.Normal, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Change(x, runId, (Difficulty)99), RejectionCodes.CommandInvalid);
    }

    [Fact]
    public void Unknown_run_is_rejected()
    {
        var (s, _) = Completed(Difficulty.Normal, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Change(x, SequentialIds.Make(0x0BAD0000, 2), Difficulty.Hard), RejectionCodes.RunUnknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_comment_is_rejected(string comment)
    {
        var (s, runId) = Completed(Difficulty.Extreme, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Change(x, runId, Difficulty.Normal, comment), RejectionCodes.CommentRequired);
        Assert.Single(s.State.ManualEffects);
    }

    [Fact]
    public void Comment_over_the_limit_is_rejected()
    {
        var (s, runId) = Completed(Difficulty.Normal, [3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Change(x, runId, Difficulty.Hard, new string('я', Limits.MaxCommentLength + 1)), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Comment_at_the_limit_is_accepted()
    {
        var (s, runId) = Completed(Difficulty.Normal, [3, 1]);
        var comment = new string('я', Limits.MaxCommentLength);

        Change(s, runId, Difficulty.Hard, comment);

        ScenarioAssert.Accepted(s);
        Assert.Equal(comment, Assert.Single(s.LastEvents<RunDifficultyChanged>()).Comment);
    }

    [Fact]
    public void Change_is_allowed_while_the_season_is_closing()
    {
        var (s, runId) = Completed(Difficulty.Hard, [5, 2]);
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);

        Change(s, runId, Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Equal(6, s.Player("Вася").Points);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Change_after_the_season_is_finished_is_rejected(SeasonStatus status)
    {
        var (s, runId) = Completed(Difficulty.Hard, [5, 2]);
        s.MoveStatusToForcingFinish(status); // the run stays unchecked on purpose

        ScenarioAssert.RejectsWithoutChanges(s, x => Change(x, runId, Difficulty.Normal), RejectionCodes.SeasonClosed);
    }

    // ---- With hours corrections ----

    [Fact]
    public void Hours_corrected_after_a_difficulty_change_add_dice_of_the_new_difficulty()
    {
        var (s, runId) = Completed(Difficulty.Normal, [3, 1]);
        Change(s, runId, Difficulty.Hard);

        s.NextRandom(6, 6);
        s.Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(6, 6), new Die(6, 6)], Assert.Single(s.LastEvents<RunHoursCorrected>()).Added);
        Assert.Equal([new Die(6, 5), new Die(6, 2), new Die(6, 6), new Die(6, 6)], s.State.Runs[runId].Dice);
        Assert.Equal(19, s.Player("Вася").Points);
    }

    // ---- The log ----

    [Fact]
    public void Replaying_the_log_gives_the_same_state_after_changes()
    {
        var (s, runId) = Completed(Difficulty.Extreme, [5, 2], challengeDice: [6]);
        Change(s, runId, Difficulty.Normal);
        Change(s, runId, Difficulty.Extreme);
        Change(s, runId, Difficulty.Easy);
        ScenarioAssert.Accepted(s);

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.State, replayed);
        Assert.Empty(replayed.ManualEffects);
        Assert.Equal(Difficulty.Easy, replayed.Runs[runId].Difficulty);
    }

    [Fact]
    public void Folding_the_change_events_alone_updates_the_run_the_player_and_the_effects()
    {
        var (s, runId) = Completed(Difficulty.Extreme, [5, 2]);
        var before = s.State;

        Change(s, runId, Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.State, s.Last.Events.Aggregate(before, SeasonEngine.Apply));
    }

    [Fact]
    public void Folding_a_resolution_removes_only_that_effect()
    {
        // ManualEffectResolved on its own (D-97: the minimum of C11)
        var (s, runId) = Completed(Difficulty.Extreme, [5, 2]);
        var effectId = PendingEffectOf(s, runId);

        var after = SeasonEngine.Apply(s.State, new ManualEffectResolved(effectId, s.PlayerId("Вася"), runId, ManualEffectOutcome.Applied, "Разыграли"));

        Assert.Empty(after.ManualEffects);
        Assert.Equal(s.State with { ManualEffects = after.ManualEffects }, after);
    }
}
