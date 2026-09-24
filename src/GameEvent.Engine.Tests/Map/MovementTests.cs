using GameEvent.Engine.Map;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// Moving back (SPEC «Движение», D-90): along the walked edges of the last path segment; when the segment runs out,
/// along the primary backward edge (or the only incoming edge); never across a segment boundary and never past the
/// start — missing steps are lost (M2, RR3). The result is the cells entered, in order.
/// </summary>
public class MovementTests
{
    /// <summary>
    /// A merge: two branches from the start meet at <c>m</c>. <c>m</c> has two incoming edges and only
    /// <c>a1 → m</c> is primary; every other cell has a single incoming edge.
    /// <code>
    /// start → a1 → m → x → finish
    /// start → b1 ↗
    /// </code>
    /// </summary>
    private static MapGraph MergeMap() => new(
        [
            new Cell("start", CellType.Start),
            new Cell("a1", CellType.Empty),
            new Cell("b1", CellType.Empty),
            new Cell("m", CellType.Empty),
            new Cell("x", CellType.Empty),
            new Cell("finish", CellType.Finish),
        ],
        [
            new Edge("start", "a1", IsDefaultForward: true, IsPrimaryBackward: true),
            new Edge("start", "b1", IsDefaultForward: false, IsPrimaryBackward: true),
            new Edge("a1", "m", IsDefaultForward: true, IsPrimaryBackward: true),
            new Edge("b1", "m", IsDefaultForward: true, IsPrimaryBackward: false),
            new Edge("m", "x", IsDefaultForward: true, IsPrimaryBackward: true),
            new Edge("x", "finish", IsDefaultForward: true, IsPrimaryBackward: true),
        ]);

    private static PlayerPath Path(params string[][] segments) =>
        new([.. segments.Select(cells => new PathSegment([.. cells]))]);

    // ---- Along the walked edges ----

    [Fact]
    public void Back_retraces_the_walked_cells_of_the_last_segment()
    {
        var map = LinearMap.Generate(8);
        var path = Path(["start", "c1", "c2", "c3", "c4", "c5"]);

        Assert.Equal(["c4", "c3"], Movement.Backward(map, path, 2));
    }

    [Fact]
    public void Back_through_a_merge_follows_the_walked_branch_not_the_primary_one()
    {
        // Given the player came through b1 (the non-primary way into m)
        var map = MergeMap();
        var path = Path(["start", "b1", "m", "x"]);

        // When pushed back three steps, they go back the way they came
        Assert.Equal(["m", "b1", "start"], Movement.Backward(map, path, 3));
    }

    [Fact]
    public void Back_to_the_first_cell_of_the_segment_uses_only_history()
    {
        var map = MergeMap();
        var path = Path(["start", "b1", "m"]);

        Assert.Equal(["b1"], Movement.Backward(map, path, 1));
    }

    // ---- When the history runs out ----

    [Fact]
    public void Back_without_history_follows_the_primary_backward_edge_at_a_merge()
    {
        // Given the player was put on m (a new segment of one cell)
        var map = MergeMap();
        var path = Path(["start"], ["m"]);

        // Then back goes along a1 → m, the primary incoming edge, not b1 → m
        Assert.Equal(["a1", "start"], Movement.Backward(map, path, 2));
    }

    [Fact]
    public void Back_without_history_follows_the_only_incoming_edge()
    {
        var map = LinearMap.Generate(8);
        var path = Path(["start"], ["c5"]);

        Assert.Equal(["c4", "c3", "c2"], Movement.Backward(map, path, 3));
    }

    [Fact]
    public void Back_past_the_walked_history_continues_along_primary_edges()
    {
        // Given the player was put on m, then walked m → x
        var map = MergeMap();
        var path = Path(["start"], ["m", "x"]);

        // x → m is walked; at m the segment is exhausted, so a1 (primary), then start
        Assert.Equal(["m", "a1", "start"], Movement.Backward(map, path, 3));
    }

    [Fact]
    public void Back_does_not_cross_a_segment_boundary()
    {
        // Given the player walked start → b1, then was transferred to x
        var map = MergeMap();
        var path = Path(["start", "b1"], ["x"]);

        // Then back from x ignores the pre-transfer cells: a transfer is not an edge
        Assert.Equal(["m", "a1", "start"], Movement.Backward(map, path, 3));
    }

    [Fact]
    public void Back_after_a_transfer_ignores_the_walked_branch_before_it()
    {
        // Given the player walked through b1 to m, then was transferred to a later cell and walked on
        var map = MergeMap();
        var path = Path(["start", "b1", "m"], ["x", "finish"]);

        // Then back from the finish: finish → x is walked, then primary edges (m, a1), not b1
        Assert.Equal(["x", "m", "a1", "start"], Movement.Backward(map, path, 4));
    }

    // ---- The start (M2, RR3) ----

    [Fact]
    public void Back_clamped_at_start()
    {
        var map = LinearMap.Generate(8);
        var path = Path(["start", "c1", "c2"]);

        // Five steps back from c2: only two cells to enter, the other three are lost
        Assert.Equal(["c1", "start"], Movement.Backward(map, path, 5));
    }

    [Fact]
    public void Back_clamped_at_start_without_history()
    {
        var map = LinearMap.Generate(8);
        var path = Path(["start"], ["c2"]);

        Assert.Equal(["c1", "start"], Movement.Backward(map, path, 10));
    }

    [Fact]
    public void Back_exactly_to_start_ends_on_start()
    {
        var map = LinearMap.Generate(8);
        var path = Path(["start", "c1", "c2", "c3"]);

        Assert.Equal(["c2", "c1", "start"], Movement.Backward(map, path, 3));
    }

    [Fact]
    public void Back_from_start_goes_nowhere()
    {
        var map = LinearMap.Generate(8);

        Assert.Empty(Movement.Backward(map, PlayerPath.At("start"), 3));
    }

    [Fact]
    public void Back_from_start_after_walking_back_to_it_goes_nowhere()
    {
        // The start is reached along a primary edge after a transfer: still nothing behind it
        var map = MergeMap();
        var path = Path(["start"], ["a1"], ["start"]);

        Assert.Empty(Movement.Backward(map, path, 2));
    }

    [Fact]
    public void Back_zero_steps_goes_nowhere()
    {
        var map = LinearMap.Generate(8);
        var path = Path(["start", "c1", "c2"]);

        Assert.Empty(Movement.Backward(map, path, 0));
    }

    [Fact]
    public void Drop_penalty_does_not_go_past_start()
    {
        // RR3, movement part: a player placed mid-season on c2 who walked one cell gets a penalty far larger
        // than the map behind them: back along the walked edge, then along primary edges, and stop on the start
        var map = LinearMap.Generate(8);
        var path = PlayerPath.At("start")
            .After(new PlayerMoved(Guid.Empty, "start", "c2", 0, ["c2"], MoveReason.AdminAdjustment, RunId: null))
            .After(new PlayerMoved(Guid.Empty, "c2", "c3", 1, ["c3"], MoveReason.CompletionRoll, RunId: null));

        var entered = Movement.Backward(map, path, 12);

        Assert.Equal(["c2", "c1", "start"], entered);
    }

    [Fact]
    public void Back_from_the_finish_retraces_the_last_steps()
    {
        var map = LinearMap.Generate(3);
        var path = Path(["start", "c1", "c2", "finish"]);

        Assert.Equal(["c2"], Movement.Backward(map, path, 1));
    }
}
