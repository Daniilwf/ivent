using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// Cells on a stop (SPEC «Движение»: клетки срабатывают при любой остановке, включая чужой толчок, кроме клетки
/// назначения телепорта; D-303) and checkpoints (SPEC «Чекпоинт»; D-306).
/// </summary>
public class CellStopTests
{
    private static Scenario Pool(MapGraph map) =>
        Scenario.New().WithMap(map)
            .WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror").WithGame("Kuon", 6, "Horror")
            .WithPlayers("Вася", "Петя");

    /// <summary>Вася rolls <paramref name="title"/>, plays it and throws <paramref name="dice"/> (d4 each).</summary>
    private static Scenario Throw(Scenario s, string title, params int[] dice) =>
        s.RollTitle("Вася", title).Start("Вася").NextRandom(dice).Complete("Вася");

    private static Guid LastRun(Scenario s) =>
        s.State.Runs.Values.Where(r => r.PlayerId == s.PlayerId("Вася")).OrderBy(r => r.StartedAt).Last().RunId;

    // ---- Teleports ----

    /// <summary>start → a → t → b → c → d → e → finish, t a shortcut to d; d is a points bonus that the destination never gives.</summary>
    private static MapGraph Shortcut() =>
        MapBuilder.New().Path("start", "a", "t", "b", "c", "d", "e", "finish").Teleport("t", to: "d").Bonus("d", 5).Build();

    [Fact]
    public void Stop_on_a_teleport_transfers_and_the_destination_does_not_trigger()
    {
        var s = Pool(Shortcut());

        Throw(s, "Silent Hill", 2);

        ScenarioAssert.Accepted(s);
        var moves = s.LastEvents<PlayerMoved>().ToList();
        Assert.Equal(2, moves.Count);
        Assert.Equal(new PlayerMoved(s.PlayerId("Вася"), "t", "d", 0, ["d"], MoveReason.Teleport, RunId: null), moves[1]);
        Assert.Equal("d", s.Player("Вася").CellId);

        // The bonus on d is not given: a transfer's destination does not trigger
        Assert.Equal(2, s.Player("Вася").Points);
        Assert.DoesNotContain(s.LastEvents<PointsChanged>(), p => p.Reason == PointsReason.CellBonus);

        // A new path segment starts at the destination
        Assert.Equal(["d"], s.Player("Вася").Path.Segments[^1].Cells);
        Assert.Equal(2, s.Player("Вася").Path.Segments.Count);
        Assert.Empty(Movement.Visits(moves[1]));
    }

    [Fact]
    public void Passing_a_teleport_does_not_transfer()
    {
        var s = Pool(Shortcut());

        Throw(s, "Silent Hill", 3);

        Assert.Equal("b", s.Player("Вася").CellId);
        Assert.Single(s.LastEvents<PlayerMoved>());
    }

    [Fact]
    public void Snake_sends_the_player_back_and_the_run_keeps_its_own_cells()
    {
        // start → a → b → s → c → finish, s a snake to a
        var map = MapBuilder.New().Path("start", "a", "b", "s", "c", "finish").Teleport("s", to: "a").Build();
        var s = Pool(map);

        Throw(s, "Silent Hill", 3);

        Assert.Equal("a", s.Player("Вася").CellId);
        Assert.Equal(3, s.Player("Вася").Points);
        Assert.Equal(3, s.State.Runs[LastRun(s)].Moved);
    }

    [Fact]
    public void Drop_penalty_stopping_on_a_teleport_transfers()
    {
        // The push back is a stop too (SPEC: включая чужой толчок). start → a → s → b → c → d → finish, s a snake to start
        var map = MapBuilder.New().Path("start", "a", "s", "b", "c", "d", "finish").Teleport("s", to: "start").Build();
        var s = Pool(map);
        Throw(s, "Kuon", 2, 2);
        Assert.Equal("c", s.Player("Вася").CellId);

        // Penalty 1 + 1 = 2: c → b → s, then the snake
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(["b", "s"], s.LastEvents<PlayerMoved>().First().Path);
        Assert.Equal(MoveReason.Teleport, s.LastEvents<PlayerMoved>().Last().Reason);
        Assert.Equal("start", s.Player("Вася").CellId);
    }

    [Fact]
    public void Correction_moving_forward_onto_a_teleport_or_a_bonus_does_not_trigger()
    {
        // D-303: a correction adjusts history, it is not a move of the game
        var map = MapBuilder.New().Path("start", "a", "t", "b", "p", "c", "d", "finish").Teleport("t", to: "d").Bonus("p", 5).Build();
        var s = Pool(map);
        Throw(s, "Silent Hill", 1);
        s.NextRandom(1).Act(new CorrectRunHours(LastRun(s), 6, "часы"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(("t", 2), (s.Player("Вася").CellId, s.Player("Вася").Points));

        s.NextRandom(2).Act(new CorrectRunHours(LastRun(s), 9, "часы"));
        Assert.Equal(("p", 4), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Admin_transfer_onto_a_teleport_does_not_trigger()
    {
        var s = Pool(Shortcut());

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "t"));

        ScenarioAssert.Accepted(s);
        Assert.Equal("t", s.Player("Вася").CellId);
    }

    // ---- Points bonus ----

    /// <summary>start → a → p → b → finish, p gives 3 points.</summary>
    private static MapGraph BonusMap() => MapBuilder.New().Path("start", "a", "p", "b", "c", "d", "finish").Bonus("p", 3).Build();

    [Fact]
    public void Stop_on_a_bonus_gives_its_points_and_a_pass_does_not()
    {
        var s = Pool(BonusMap());

        Throw(s, "Silent Hill", 2);

        Assert.Equal(new PointsChanged(s.PlayerId("Вася"), 3, PointsReason.CellBonus, RunId: null), s.LastEvents<PointsChanged>().Last());
        Assert.Equal(5, s.Player("Вася").Points);

        Throw(s, "Fatal Frame", 1);
        Assert.Equal(6, s.Player("Вася").Points);
    }

    [Fact]
    public void Passing_a_bonus_gives_nothing()
    {
        var s = Pool(BonusMap());

        Throw(s, "Silent Hill", 3);

        Assert.Equal(3, s.Player("Вася").Points);
    }

    [Fact]
    public void Bonus_stays_when_the_run_is_rejected()
    {
        // SPEC «Реджект»: остальные последствия (ивенты клеток) остаются
        var s = Pool(BonusMap());
        Throw(s, "Silent Hill", 2);

        s.Act(new RejectProof(LastRun(s), "нет пруфа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(("start", 3), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Reject_moving_back_onto_a_bonus_does_not_trigger_it()
    {
        // D-303: a reject corrects history, it is not a move of the game
        var s = Pool(BonusMap());
        Throw(s, "Silent Hill", 2);
        Throw(s, "Fatal Frame", 2);
        Assert.Equal(("c", 7), (s.Player("Вася").CellId, s.Player("Вася").Points));

        s.Act(new RejectProof(LastRun(s), "нет пруфа"));

        Assert.Equal(("p", 5), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Drop_penalty_stopping_on_a_bonus_gives_it()
    {
        var s = Pool(BonusMap());
        Throw(s, "Kuon", 3, 2);
        Assert.Equal("d", s.Player("Вася").CellId);

        // Penalty 1 + 2 = 3: d → c → b → p
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 2).Act(new DropRun(s.PlayerId("Вася")));

        Assert.Equal("p", s.Player("Вася").CellId);
        Assert.Equal(5 - 3 + 3, s.Player("Вася").Points);
    }

    // ---- Checkpoints (D-306) ----

    /// <summary>start → a → k → b → c → d → finish, k a checkpoint.</summary>
    private static MapGraph CheckpointMap() => MapBuilder.New().Path("start", "a", "k", "b", "c", "d", "finish").Checkpoint("k").Build();

    [Fact]
    public void Drop_penalty_stops_on_the_checkpoint_passed()
    {
        var s = Pool(CheckpointMap());
        Throw(s, "Kuon", 2, 2);
        Assert.Equal("c", s.Player("Вася").CellId);

        // Penalty 4 + 4 = 8: c → b → k and no further
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(4, 4).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(["b", "k"], Assert.Single(s.LastEvents<PlayerMoved>()).Path);
        Assert.Equal("k", s.Player("Вася").CellId);

        // The points take the whole penalty: the checkpoint protects the position, not the points
        Assert.Equal(4 - 8, s.Player("Вася").Points);
    }

    [Fact]
    public void Player_on_a_checkpoint_is_not_pushed_back()
    {
        var s = Pool(CheckpointMap());
        Throw(s, "Silent Hill", 2);
        Assert.Equal("k", s.Player("Вася").CellId);

        s.RollTitle("Вася", "Fatal Frame").Start("Вася").NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal("k", s.Player("Вася").CellId);
    }

    [Fact]
    public void Checkpoint_does_not_keep_the_cells_of_a_rejected_run()
    {
        // D-306: a reject takes back what the run gave, a checkpoint reached by it included
        var s = Pool(CheckpointMap());
        Throw(s, "Kuon", 2, 2);

        s.Act(new RejectProof(LastRun(s), "нет пруфа"));

        Assert.Equal("start", s.Player("Вася").CellId);
    }

    [Fact]
    public void Checkpoint_behind_a_teleport_stops_a_push_along_the_primary_edges()
    {
        // start → a → k → b → c → t → d → e → finish; t sends back to b. After the transfer the path is [b]:
        // moving back follows the primary incoming edges and stops on the checkpoint k.
        var map = MapBuilder.New().Path("start", "a", "k", "b", "c", "t", "d", "e", "finish").Checkpoint("k").Teleport("t", to: "b").Build();
        var s = Pool(map);
        Throw(s, "Kuon", 3, 2);
        Assert.Equal("b", s.Player("Вася").CellId);

        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(4, 4).Act(new DropRun(s.PlayerId("Вася")));

        Assert.Equal("k", s.Player("Вася").CellId);
    }
}
