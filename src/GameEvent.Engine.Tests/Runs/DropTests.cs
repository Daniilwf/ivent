using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// «Дроп» (SPEC «Реролл, дроп, тех-реролл»: penalty dice take points and position, a mandatory bad event, no coins;
/// «Движение»: back along the walked path, never past the start; «Статусы игры в сезоне»: one's own dropped game never
/// comes back, a game dropped by another is free; D-09, D-90, D-94). One command writes
/// <see cref="RunDropped"/> (the penalty dice one by one), <see cref="PointsChanged"/> (DropPenalty),
/// <see cref="PlayerMoved"/> back (DropPenalty), <see cref="GameExcluded"/> (Dropped) and
/// <see cref="ManualEffectCreated"/> (Bad, Drop). The pinned test ruleset: penalty 2d4, both parts on, a bad event,
/// normal difficulty d4, 3 hours per die. Every game here is 12 hours: a completion throws 4 dice.
/// </summary>
public class DropTests
{
    private static readonly string[] s_horror = ["Silent Hill", "Alan Wake", "Dead Space"];

    private static Scenario Season(Func<Ruleset, Ruleset>? rules = null, params string[] games)
    {
        var s = Scenario.New();
        if (rules is not null)
        {
            s.WithRuleset(rules);
        }

        s.WithCategory("Horror");
        foreach (var game in games.Length == 0 ? s_horror : games)
        {
            s.WithGame(game, 12, "Horror");
        }

        return s.WithPlayers("Вася", "Петя");
    }

    /// <summary>The player completes a game whose four d4 show <paramref name="dice"/>: points and cells grow by their sum.</summary>
    private static Scenario Walk(Scenario s, string player, params int[] dice) =>
        s.Roll(player).Start(player).NextRandom(dice).Complete(player, Difficulty.Normal);

    private static Scenario Playing(Scenario s, string player) => s.Roll(player).Start(player);

    private static Guid ActiveRun(Scenario s, string player) => s.Player(player).ActiveRunId!.Value;

    private static Scenario Drop(Scenario s, string player) => s.Act(new DropRun(s.PlayerId(player)));

    private static Func<Ruleset, Ruleset> DropRules(Func<DropRules, DropRules> change) =>
        r => r with { Drop = change(r.Drop) };

    // ---- The whole penalty ----

    [Fact]
    public void Drop_takes_points_and_position_by_the_penalty_dice_and_creates_a_bad_event()
    {
        // Given Вася walked to c7 with 7 points and is playing another game
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        var vasya = s.PlayerId("Вася");
        var runId = ActiveRun(s, "Вася");
        var game = s.State.Runs[runId].GameId;
        s.Advance(TimeSpan.FromHours(2));

        // When he drops and the penalty dice show 3 and 2 (a third scripted value must stay unused)
        s.NextRandom(3, 2, 4);
        Drop(s, "Вася");

        // Then exactly: RunDropped, PointsChanged(-5), PlayerMoved back 5, GameExcluded(Dropped), ManualEffectCreated(Bad, Drop)
        ScenarioAssert.Accepted(s);
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Equal(5, s.Last.Events.Count);
        Assert.Equal(new RunDropped(runId, vasya, [new Die(4, 3), new Die(4, 2)], s.Clock.UtcNow), s.Last.Events[0]);
        Assert.Equal(new PointsChanged(vasya, -5, PointsReason.DropPenalty, runId), s.Last.Events[1]);
        Assert.Equal(
            new PlayerMoved(vasya, "c7", "c2", -5, ["c6", "c5", "c4", "c3", "c2"], MoveReason.DropPenalty, runId),
            s.Last.Events[2]);
        Assert.Equal(new GameExcluded(vasya, game, ExclusionReason.Dropped), s.Last.Events[3]);
        var created = Assert.IsType<ManualEffectCreated>(s.Last.Events[4]);
        Assert.NotEqual(Guid.Empty, created.EffectId);
        Assert.Equal(new ManualEffectCreated(created.EffectId, vasya, EventKind.Bad, ManualEffectSource.Drop, runId), created);

        // And Вася is Idle with 2 points on c2; the run is dropped; the bad event waits for him
        var player = s.Player("Вася");
        Assert.Equal(TurnPhase.Idle, player.Phase);
        Assert.Null(player.ActiveRunId);
        Assert.Null(player.Offer);
        Assert.Equal(2, player.Points);
        Assert.Equal("c2", player.CellId);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
        Assert.Contains(new GameExclusion(game, ExclusionReason.Dropped), player.Exclusions);
        Assert.Equal(
            new PendingManualEffect(created.EffectId, vasya, EventKind.Bad, ManualEffectSource.Drop, runId),
            s.State.ManualEffects[created.EffectId]);
    }

    [Fact]
    public void Drop_gives_no_coins_and_takes_none()
    {
        // Given Вася has 7 coins
        var s = Season();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Приз", CoinsDelta: 7));
        Playing(s, "Вася");

        s.NextRandom(4, 4);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Empty(s.LastEvents<ResourceChanged>());
        Assert.Equal(7, s.Player("Вася").Coins);
    }

    [Fact]
    public void Penalty_dice_follow_the_ruleset()
    {
        // Given the penalty is 3d6
        var s = Season(DropRules(d => d with { PenaltyDice = new PenaltyDice { Count = 3, Sides = 6 } }));
        Walk(s, "Вася", 4, 4, 4, 4);
        Playing(s, "Вася");
        var runId = ActiveRun(s, "Вася");

        s.NextRandom(6, 1, 5);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(6, 6), new Die(6, 1), new Die(6, 5)], Assert.Single(s.LastEvents<RunDropped>()).PenaltyDice);
        Assert.Equal(new PointsChanged(s.PlayerId("Вася"), -12, PointsReason.DropPenalty, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(-12, Assert.Single(s.LastEvents<PlayerMoved>()).Steps);
        Assert.Equal(4, s.Player("Вася").Points);
        Assert.Equal("c4", s.Player("Вася").CellId);
    }

    // ---- Points may go negative (D-09) ----

    [Fact]
    public void Points_can_go_negative()
    {
        // Given Вася is on the start with 0 points
        var s = Playing(Season(), "Вася");
        var runId = ActiveRun(s, "Вася");

        s.NextRandom(3, 2);
        Drop(s, "Вася");

        // Then the points go to -5: an early drop is not free (D-09)
        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(s.PlayerId("Вася"), -5, PointsReason.DropPenalty, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(-5, s.Player("Вася").Points);
    }

    // ---- Position: never past the start, back along the walked path (RR3, D-90) ----

    [Fact]
    public void Position_clamped_at_start()
    {
        // Given Вася stands on c4 with 4 points
        var s = Walk(Season(), "Вася", 1, 1, 1, 1);
        Playing(s, "Вася");
        var vasya = s.PlayerId("Вася");
        var runId = ActiveRun(s, "Вася");

        // When the penalty is 8
        s.NextRandom(4, 4);
        Drop(s, "Вася");

        // Then the token stops on the start (the missing steps are lost), the points take the whole 8
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(vasya, "c4", LinearMap.StartId, -8, ["c3", "c2", "c1", LinearMap.StartId], MoveReason.DropPenalty, runId),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(LinearMap.StartId, s.Player("Вася").CellId);
        Assert.Equal(-4, s.Player("Вася").Points);
    }

    [Fact]
    public void On_the_start_a_drop_writes_no_move()
    {
        // D-47: a move that enters no cell writes no event
        var s = Playing(Season(), "Вася");
        var vasya = s.PlayerId("Вася");
        var runId = ActiveRun(s, "Вася");
        var game = s.State.Runs[runId].GameId;

        s.NextRandom(1, 1);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunDropped), typeof(PointsChanged), typeof(GameExcluded), typeof(ManualEffectCreated)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new GameExcluded(vasya, game, ExclusionReason.Dropped), s.Last.Events[2]);
        Assert.Equal(LinearMap.StartId, s.Player("Вася").CellId);
        Assert.Equal(PlayerPath.At(LinearMap.StartId), s.Player("Вася").Path);
    }

    [Fact]
    public void Moving_back_retraces_the_walked_path()
    {
        // Given Вася walked start → c7 in one segment
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        var pathBefore = s.Player("Вася").Path;

        s.NextRandom(2, 1);
        Drop(s, "Вася");

        // Then the move goes back over the walked cells, the same cells Movement.Backward gives for that path
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(["c6", "c5", "c4"], moved.Path);
        Assert.Equal(Movement.Backward(s.State.Map, pathBefore, 3), moved.Path);
        Assert.Equal(pathBefore.After(moved), s.Player("Вася").Path);
        Assert.Equal(new PlayerPath([new PathSegment(["start", "c1", "c2", "c3", "c4"])]), s.Player("Вася").Path);
    }

    [Fact]
    public void Moving_back_after_a_transfer_follows_the_primary_incoming_edges()
    {
        // Given Вася walked to c4, then the admin moved him to c10 (a transfer starts a new segment, D-90)
        var s = Walk(Season(), "Вася", 1, 1, 1, 1);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "c10"));
        ScenarioAssert.Accepted(s);
        Playing(s, "Вася");
        var pathBefore = s.Player("Вася").Path;
        Assert.Equal(2, pathBefore.Segments.Count);

        // When the penalty is 2
        s.NextRandom(1, 1);
        Drop(s, "Вася");

        // Then the token goes back by the map's primary incoming edges, not to the walked c4
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(("c10", "c8", -2), (moved.From, moved.To, moved.Steps));
        Assert.Equal(["c9", "c8"], moved.Path);
        var path = s.Player("Вася").Path;
        Assert.Equal(pathBefore.After(moved), path);
        Assert.Equal("c8", path.Current);
        Assert.Equal(pathBefore.Segments[0], path.Segments[0]);
    }

    // ---- Parts switched off by the ruleset ----

    [Fact]
    public void Without_affects_points_the_drop_keeps_the_points()
    {
        var s = Season(DropRules(d => d with { AffectsPoints = false }));
        Walk(s, "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");

        s.NextRandom(3, 2);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PointsChanged>());
        Assert.Equal(7, s.Player("Вася").Points);
        Assert.Equal("c2", s.Player("Вася").CellId);
        Assert.Equal(
            [typeof(RunDropped), typeof(PlayerMoved), typeof(GameExcluded), typeof(ManualEffectCreated)],
            s.Last.Events.Select(e => e.GetType()));
    }

    [Fact]
    public void Without_affects_position_the_drop_keeps_the_token()
    {
        var s = Season(DropRules(d => d with { AffectsPosition = false }));
        Walk(s, "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");

        s.NextRandom(3, 2);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal("c7", s.Player("Вася").CellId);
        Assert.Equal(2, s.Player("Вася").Points);
        Assert.Equal(
            [typeof(RunDropped), typeof(PointsChanged), typeof(GameExcluded), typeof(ManualEffectCreated)],
            s.Last.Events.Select(e => e.GetType()));
    }

    [Fact]
    public void Without_a_mandatory_event_the_drop_creates_no_manual_effect()
    {
        var s = Season(DropRules(d => d with { MandatoryEvent = MandatoryEvent.None }));
        Walk(s, "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");

        s.NextRandom(3, 2);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
        Assert.Empty(s.State.ManualEffects);
        Assert.Equal(
            [typeof(RunDropped), typeof(PointsChanged), typeof(PlayerMoved), typeof(GameExcluded)],
            s.Last.Events.Select(e => e.GetType()));
    }

    [Fact]
    public void With_every_part_off_the_drop_only_drops_and_excludes()
    {
        var s = Season(DropRules(d => d with { AffectsPoints = false, AffectsPosition = false, MandatoryEvent = MandatoryEvent.None }));
        Walk(s, "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        var runId = ActiveRun(s, "Вася");

        s.NextRandom(3, 2);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunDropped), typeof(GameExcluded)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal((7, "c7"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }

    [Fact]
    public void Drop_follows_the_rules_in_force_at_the_drop_not_the_roll_snapshot()
    {
        // SPEC «Снапшот»: only what concerns the run is fixed at the roll; the drop penalty is the current config
        var s = Playing(Season(), "Вася");
        s.WithRuleset(DropRules(d => d with { PenaltyDice = new PenaltyDice { Count = 1, Sides = 6 } }));

        s.NextRandom(5);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(6, 5)], Assert.Single(s.LastEvents<RunDropped>()).PenaltyDice);
        Assert.Equal(-5, s.Player("Вася").Points);
    }

    // ---- Any time while playing (D-09, RR4: the hour is only a hint) ----

    [Fact]
    public void Drop_is_allowed_right_after_the_start()
    {
        var s = Playing(Season(), "Вася");
        Assert.True(s.Ruleset.Roll.MinPlayMinutesBeforeDrop > 0);

        // No time passes between the start and the drop
        s.NextRandom(1, 1);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.Clock.UtcNow, Assert.Single(s.LastEvents<RunDropped>()).DroppedAt);
    }

    [Fact]
    public void Drop_is_allowed_long_after_the_start()
    {
        var s = Playing(Season(), "Вася");
        s.Advance(TimeSpan.FromDays(10));

        s.NextRandom(1, 1);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
    }

    // ---- Game statuses (G8): own dropped game never again, free for others ----

    [Fact]
    public void Dropped_game_never_comes_to_the_player_again()
    {
        // Given the only game, dropped by Вася
        var s = Playing(Season(null, "Silent Hill"), "Вася");
        s.NextRandom(1, 1);
        Drop(s, "Вася");
        ScenarioAssert.Accepted(s);

        // Then his next roll finds nothing: the game is excluded for him
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Dropped_game_is_available_to_other_players()
    {
        // Given the only game, dropped by Вася
        var s = Playing(Season(null, "Silent Hill"), "Вася");
        s.NextRandom(1, 1);
        Drop(s, "Вася");

        // When Петя rolls he gets it, without a miss and without an exclusion of his own
        s.Roll("Петя");

        var rolled = Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal(s.GameId("Silent Hill"), rolled.GameId);
        Assert.Empty(rolled.Misses);
        Assert.Empty(s.Player("Петя").Exclusions);
    }

    [Fact]
    public void Dropped_game_is_skipped_for_the_player_among_others()
    {
        // Given Вася dropped one of three games
        var s = Playing(Season(), "Вася");
        var dropped = s.State.Runs[ActiveRun(s, "Вася")].GameId;
        s.NextRandom(1, 1);
        Drop(s, "Вася");

        // Then his next rolls never offer it, not even as a miss (D-05)
        for (var i = 0; i < 2; i++)
        {
            s.Roll("Вася");
            var rolled = Assert.Single(s.LastEvents<GameRolled>());
            Assert.NotEqual(dropped, rolled.GameId);
            Assert.DoesNotContain(rolled.Misses, m => m.GameId == dropped);
            s.Start("Вася").NextRandom(1, 1);
            Drop(s, "Вася");
            ScenarioAssert.Accepted(s);
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
        Assert.Equal(3, s.Player("Вася").Exclusions.Count);
        Assert.All(s.Player("Вася").Exclusions, x => Assert.Equal(ExclusionReason.Dropped, x.Reason));
    }

    [Fact]
    public void Drop_of_one_player_does_not_touch_another()
    {
        var s = Season();
        Walk(s, "Петя", 2, 2, 2, 2);
        Playing(s, "Петя");
        Playing(s, "Вася");
        var petya = s.Player("Петя");

        s.NextRandom(4, 4);
        Drop(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(petya, s.Player("Петя"));
        Assert.All(s.Last.Events, e => Assert.Equal(s.PlayerId("Вася"), PlayerOf(e)));
    }

    // ---- Rejections ----

    [Fact]
    public void Drop_while_idle_is_rejected_with_wrong_phase()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => Drop(x, "Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Drop_of_an_offered_game_is_rejected_with_wrong_phase()
    {
        // A game only offered is not played yet: give it up by a reroll, not a drop
        var s = Season().Roll("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Drop(x, "Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Drop_twice_is_rejected_the_second_time()
    {
        // Two tabs: the second drop finds the player Idle
        var s = Playing(Season(), "Вася");
        s.NextRandom(1, 1);
        Drop(s, "Вася");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Drop(x, "Вася"), RejectionCodes.WrongPhase);
    }

    [Fact]
    public void Drop_by_an_unknown_player_is_rejected()
    {
        var s = Playing(Season(), "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new DropRun(SequentialIds.Make(0x0BAD0000, 1))), RejectionCodes.PlayerUnknown);
    }

    [Theory]
    [InlineData(SeasonStatus.Closing)]
    [InlineData(SeasonStatus.Finished)]
    public void Drop_when_the_season_is_not_running_is_rejected(SeasonStatus status)
    {
        // SPEC «Сезон»: after the deadline no game actions
        var s = Playing(Season(), "Вася");
        for (var next = SeasonStatus.Closing; next <= status; next++)
        {
            s.Act(new ChangeSeasonStatus(next));
            ScenarioAssert.Accepted(s);
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => Drop(x, "Вася"), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Rejected_drop_throws_no_dice()
    {
        var s = Season();
        s.NextRandom(1, 1);

        ScenarioAssert.RejectsWithoutChanges(s, x => Drop(x, "Вася"), RejectionCodes.WrongPhase);
        Assert.Equal(2, s.Random.ScriptedLeft);
    }

    // ---- The log ----

    [Fact]
    public void Replaying_the_log_gives_the_same_state_after_drops()
    {
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        s.NextRandom(3, 2);
        Drop(s, "Вася");
        Playing(s, "Петя");
        s.NextRandom(4, 4);
        Drop(s, "Петя");
        ScenarioAssert.Accepted(s);

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.State, replayed);
        Assert.Equal(2, replayed.ManualEffects.Count);
        Assert.Equal(-8, replayed.Players[s.PlayerId("Петя")].Points);
        Assert.All(replayed.Runs.Values.Where(r => r.Status != RunStatus.Completed), r => Assert.Equal(RunStatus.Dropped, r.Status));
    }

    [Fact]
    public void Folding_the_drop_events_alone_updates_the_run_player_and_effects()
    {
        var s = Walk(Season(), "Вася", 2, 2, 2, 1);
        Playing(s, "Вася");
        var before = s.State;

        s.NextRandom(3, 2);
        Drop(s, "Вася");

        var folded = s.Last.Events.Aggregate(before, SeasonEngine.Apply);
        Assert.Equal(s.State, folded);
    }

    [Fact]
    public void Same_seed_gives_the_same_penalty_dice()
    {
        static Scenario Play()
        {
            var s = Playing(Season(), "Вася");
            return Drop(s, "Вася");
        }

        var first = Play();
        var second = Play();

        Assert.Equal(first.Log, second.Log);
        var dice = Assert.Single(first.LastEvents<RunDropped>()).PenaltyDice;
        Assert.Equal(2, dice.Count);
        Assert.All(dice, d =>
        {
            Assert.Equal(4, d.Sides);
            Assert.InRange(d.Value, 1, 4);
        });
    }

    private static Guid? PlayerOf(IGameEvent e) =>
        e switch
        {
            RunDropped x => x.PlayerId,
            PointsChanged x => x.PlayerId,
            PlayerMoved x => x.PlayerId,
            GameExcluded x => x.PlayerId,
            ManualEffectCreated x => x.PlayerId,
            _ => null,
        };
}
