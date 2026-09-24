using GameEvent.Engine.Map;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// The stage 1 linear map: <c>start</c>, <c>c1</c>…<c>c{N-1}</c>, <c>finish</c> with N = map.linearLength (D-03).
/// Moving forward follows the chain; steps beyond the finish burn (SPEC «Движение»).
/// </summary>
public class LinearMovementTests
{
    // ---- The map (M1) ----

    [Fact]
    public void Linear_map_is_a_chain_from_start_through_numbered_cells_to_finish()
    {
        var map = LinearMap.Generate(4);

        Assert.Equal(["start", "c1", "c2", "c3", "finish"], map.Cells.Select(c => c.Id));
        Assert.Equal(CellType.Start, map.CellById("start").Type);
        Assert.Equal(CellType.Finish, map.CellById("finish").Type);
        Assert.All(["c1", "c2", "c3"], id => Assert.Equal(CellType.Empty, map.CellById(id).Type));
        Assert.Equal(
            [("start", "c1"), ("c1", "c2"), ("c2", "c3"), ("c3", "finish")],
            map.Edges.Select(e => (e.From, e.To)));
        Assert.All(map.Edges, e => Assert.True(e.IsDefaultForward && e.IsPrimaryBackward));
    }

    [Fact]
    public void Linear_map_of_length_one_goes_straight_from_start_to_finish()
    {
        var map = LinearMap.Generate(1);

        Assert.Equal(["start", "finish"], map.Cells.Select(c => c.Id));
        Assert.Single(map.Edges);
    }

    [Fact]
    public void Season_map_length_comes_from_the_ruleset()
    {
        var s = Scenario.New().WithMapLength(7).WithPlayers("Вася");

        Assert.Equal(8, s.State.Map.Cells.Count);
        Assert.Equal("finish", s.State.Map.Cells[^1].Id);
        Assert.Equal("start", s.Player("Вася").CellId);
    }

    [Fact]
    public void Default_season_map_has_linear_length_steps()
    {
        var s = Scenario.New().WithPlayers("Вася");

        Assert.Equal(s.Ruleset.Map.LinearLength + 1, s.State.Map.Cells.Count);
        Assert.Equal($"c{s.Ruleset.Map.LinearLength - 1}", s.State.Map.Cells[^2].Id);
    }

    // ---- Movement.Forward ----

    [Fact]
    public void Forward_enters_cells_along_the_chain()
    {
        var map = LinearMap.Generate(5);

        Assert.Equal(["c1", "c2", "c3"], Movement.Forward(map, "start", 3));
        Assert.Equal(["c3", "c4"], Movement.Forward(map, "c2", 2));
    }

    [Fact]
    public void Forward_exactly_to_finish_ends_on_finish()
    {
        var map = LinearMap.Generate(5);

        Assert.Equal(["c3", "c4", "finish"], Movement.Forward(map, "c2", 3));
    }

    [Fact]
    public void Forward_extra_steps_after_finish_burn()
    {
        var map = LinearMap.Generate(5);

        Assert.Equal(["c4", "finish"], Movement.Forward(map, "c3", 5));
    }

    [Fact]
    public void Forward_from_finish_goes_nowhere()
    {
        var map = LinearMap.Generate(5);

        Assert.Empty(Movement.Forward(map, "finish", 3));
    }

    [Fact]
    public void Forward_zero_steps_goes_nowhere()
    {
        var map = LinearMap.Generate(5);

        Assert.Empty(Movement.Forward(map, "c2", 0));
    }

    // ---- Completion moves the token (M3) ----

    [Fact]
    public void Completion_overshooting_finish_stops_on_finish_but_keeps_all_points()
    {
        // Given a 5-step map and a 12-hour game: 4 dice d4
        var s = Scenario.New()
            .WithMapLength(5)
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);
        var vasya = s.PlayerId("Вася");

        // When the dice show 4 + 4 + 4 + 4 = 16
        s.NextRandom(4, 4, 4, 4).Complete("Вася", Difficulty.Normal);

        // Then the token stops on the finish (11 steps burn), points grow by the full 16
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(vasya, "start", "finish", 16, ["c1", "c2", "c3", "c4", "finish"], MoveReason.CompletionRoll, CompletedRunId(s)),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(16, Assert.Single(s.LastEvents<PointsChanged>()).Delta);
        Assert.Equal("finish", s.Player("Вася").CellId);
        Assert.Equal(16, s.Player("Вася").Points);
    }

    [Fact]
    public void Completion_landing_exactly_on_finish()
    {
        var s = Scenario.New()
            .WithMapLength(5)
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);

        // 6 hours → 2 dice d6 on hard: 3 + 2 = 5 steps
        s.NextRandom(3, 2).Complete("Вася", Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(s.PlayerId("Вася"), "start", "finish", 5, ["c1", "c2", "c3", "c4", "finish"], MoveReason.CompletionRoll, CompletedRunId(s)),
            Assert.Single(s.LastEvents<PlayerMoved>()));
    }

    [Fact]
    public void Completion_one_step_short_of_finish()
    {
        var s = Scenario.New()
            .WithMapLength(5)
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);

        s.NextRandom(2, 2).Complete("Вася", Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        Assert.Equal("c4", s.Player("Вася").CellId);
    }

    [Fact]
    public void Completion_on_the_finish_writes_no_move()
    {
        // D-47: a token that cannot move writes no PlayerMoved. Points after the finish are C9's rules.
        var s = Scenario.New()
            .WithMapLength(2)
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Fatal Frame", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        s.NextRandom(3, 3).Complete("Вася", Difficulty.Hard);
        Assert.Equal("finish", s.Player("Вася").CellId);

        s.Roll("Вася").Start("Вася").NextRandom(3, 3).Complete("Вася", Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<RunCompleted>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal("finish", s.Player("Вася").CellId);
    }

    private static Guid CompletedRunId(Scenario s) => Assert.Single(s.LastEvents<RunCompleted>()).RunId;
}
