using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// The player's own path (M4, SPEC «Движение», D-90): stored in segments of cells walked edge by edge.
/// Forward extends the last segment, back retraces it, a transfer (<c>Steps = 0</c>) starts a new one; going back
/// past the walked history restarts the segment at the cell reached.
/// </summary>
public class PathTests
{
    private static readonly Guid s_player = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static PlayerPath Path(params string[][] segments) =>
        new([.. segments.Select(cells => new PathSegment([.. cells]))]);

    private static PlayerMoved Moved(string from, int steps, params string[] path) =>
        new(s_player, from, path[^1], steps, [.. path], MoveReason.CompletionRoll, RunId: null);

    private static PlayerMoved Transferred(string from, string to) =>
        new(s_player, from, to, 0, [to], MoveReason.AdminAdjustment, RunId: null);

    // ---- PlayerPath ----

    [Fact]
    public void A_placed_player_has_one_segment_of_one_cell()
    {
        var path = PlayerPath.At("start");

        Assert.Equal(Path(["start"]), path);
        Assert.Equal("start", path.Current);
    }

    [Fact]
    public void Forward_extends_the_last_segment()
    {
        var path = PlayerPath.At("start")
            .After(Moved("start", 2, "c1", "c2"))
            .After(Moved("c2", 1, "c3"));

        Assert.Equal(Path(["start", "c1", "c2", "c3"]), path);
        Assert.Equal("c3", path.Current);
    }

    [Fact]
    public void Forward_with_extra_steps_extends_only_by_the_cells_entered()
    {
        // D-47: Steps is the dice sum, Path the cells entered; burnt steps leave no trace in the path
        var path = Path(["start", "c1", "c2", "c3"]).After(Moved("c3", 9, "finish"));

        Assert.Equal(Path(["start", "c1", "c2", "c3", "finish"]), path);
    }

    [Fact]
    public void Transfer_starts_a_new_segment()
    {
        var path = Path(["start", "c1", "c2"]).After(Transferred("c2", "c7"));

        Assert.Equal(Path(["start", "c1", "c2"], ["c7"]), path);
        Assert.Equal("c7", path.Current);
    }

    [Fact]
    public void Transfer_back_to_an_earlier_cell_also_starts_a_new_segment()
    {
        // A transfer is never a step, even onto the previous cell of the segment
        var path = Path(["start", "c1", "c2"]).After(Transferred("c2", "c1"));

        Assert.Equal(Path(["start", "c1", "c2"], ["c1"]), path);
    }

    [Fact]
    public void Forward_after_a_transfer_extends_the_new_segment()
    {
        var path = Path(["start", "c1"], ["c5"]).After(Moved("c5", 2, "c6", "c7"));

        Assert.Equal(Path(["start", "c1"], ["c5", "c6", "c7"]), path);
    }

    [Fact]
    public void Back_retraces_the_last_segment()
    {
        var path = Path(["start", "c1", "c2", "c3", "c4"]).After(Moved("c4", -2, "c3", "c2"));

        Assert.Equal(Path(["start", "c1", "c2"]), path);
        Assert.Equal("c2", path.Current);
    }

    [Fact]
    public void Back_to_the_first_cell_leaves_a_segment_of_one_cell()
    {
        var path = Path(["start"], ["c5", "c6"]).After(Moved("c6", -1, "c5"));

        Assert.Equal(Path(["start"], ["c5"]), path);
    }

    [Fact]
    public void Back_past_the_walked_history_restarts_the_segment_at_the_cell_reached()
    {
        // Given c5, c6 were walked after a transfer to c5
        // When pushed back three: c5 (walked), then c4 and c3 along primary edges
        var path = Path(["start", "c1"], ["c5", "c6"]).After(Moved("c6", -3, "c5", "c4", "c3"));

        // Then the older segment is untouched and the last one starts anew at c3
        Assert.Equal(Path(["start", "c1"], ["c3"]), path);
        Assert.Equal("c3", path.Current);
    }

    [Fact]
    public void Back_past_the_walked_history_then_forward_extends_the_restarted_segment()
    {
        var path = Path(["start"], ["c5"])
            .After(Moved("c5", -2, "c4", "c3"))
            .After(Moved("c3", 3, "c4", "c5", "c6"));

        Assert.Equal(Path(["start"], ["c3", "c4", "c5", "c6"]), path);
    }

    [Fact]
    public void Back_through_a_merge_along_the_walked_branch_pops_it()
    {
        // The path remembers b1, so a later push back from m goes through b1 again
        var path = Path(["start", "b1", "m", "x"]).After(Moved("x", -1, "m"));

        Assert.Equal(Path(["start", "b1", "m"]), path);
    }

    [Fact]
    public void Paths_with_the_same_segments_are_equal()
    {
        Assert.Equal(Path(["start", "c1"], ["c5"]), Path(["start", "c1"], ["c5"]));
        Assert.NotEqual(Path(["start", "c1"], ["c5"]), Path(["start", "c1", "c5"]));
        Assert.Equal(Path(["start", "c1"]).GetHashCode(), Path(["start", "c1"]).GetHashCode());
    }

    [Fact]
    public void Path_survives_a_json_round_trip()
    {
        // The projection stores it as SeasonPlayer.PathJson (D-90)
        var path = Path(["start", "c1", "c2"], ["c7", "c8"], ["c3"]);

        var json = JsonSerializer.Serialize(path, EngineJson.Options);
        var back = JsonSerializer.Deserialize<PlayerPath>(json, EngineJson.Options);

        Assert.Equal(path, back);
        Assert.Equal("c3", back!.Current);
    }

    [Fact]
    public void Path_json_has_no_derived_current_cell()
    {
        // Only segments are stored: the current cell is derived and cannot disagree with them
        var json = JsonSerializer.Serialize(Path(["start", "c1"]), EngineJson.Options);

        Assert.Equal("""{"segments":[{"cells":["start","c1"]}]}""", json);
    }

    // ---- In the season ----

    [Fact]
    public void A_new_player_path_is_the_start_cell()
    {
        var s = Scenario.New().WithMapLength(5).WithPlayers("Вася");

        Assert.Equal(PlayerPath.At("start"), s.Player("Вася").Path);
        Assert.Equal(s.Player("Вася").CellId, s.Player("Вася").Path.Current);
    }

    [Fact]
    public void A_player_added_mid_season_on_a_cell_starts_a_new_segment_there()
    {
        var s = Scenario.New().WithMapLength(10).WithPlayers("Вася");

        s.Act(new AddSeasonPlayer(Guid.Parse("10000000-0000-0000-0000-000000000099"), Guid.Parse("40000000-0000-0000-0000-000000000099"), "Лёша", CellId: "c5"));

        ScenarioAssert.Accepted(s);
        var late = s.State.Players[Guid.Parse("10000000-0000-0000-0000-000000000099")];
        Assert.Equal(Path(["start"], ["c5"]), late.Path);
        Assert.Equal("c5", late.Path.Current);
    }

    [Fact]
    public void Completion_roll_extends_the_path()
    {
        var s = CompletedFourSteps();

        Assert.Equal(Path(["start", "c1", "c2", "c3", "c4"]), s.Player("Вася").Path);
        Assert.Equal("c4", s.Player("Вася").Path.Current);
    }

    [Fact]
    public void Admin_transfer_starts_a_new_segment_and_next_completion_extends_it()
    {
        var s = CompletedFourSteps();

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "ошибка в часах", CellId: "c2"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Path(["start", "c1", "c2", "c3", "c4"], ["c2"]), s.Player("Вася").Path);
        Assert.Equal("c2", s.Player("Вася").CellId);

        // The next completion walks on from c2 inside the new segment: 1 + 2 = 3 steps on hard
        s.Roll("Вася").Start("Вася").NextRandom(1, 2).Complete("Вася", Difficulty.Hard);

        Assert.Equal(Path(["start", "c1", "c2", "c3", "c4"], ["c2", "c3", "c4", "c5"]), s.Player("Вася").Path);
        Assert.Equal("c5", s.Player("Вася").CellId);
    }

    [Fact]
    public void Adjustment_without_a_cell_leaves_the_path_alone()
    {
        var s = CompletedFourSteps();
        var before = s.Player("Вася").Path;

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "бонус", PointsDelta: 5));

        ScenarioAssert.Accepted(s);
        Assert.Equal(before, s.Player("Вася").Path);
    }

    [Fact]
    public void Replaying_the_log_restores_the_path()
    {
        var s = CompletedFourSteps();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "перенос", CellId: "c7"));
        ScenarioAssert.Accepted(s);

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.Player("Вася").Path, replayed.Players[s.PlayerId("Вася")].Path);
        Assert.Equal(s.State, replayed);
    }

    [Fact]
    public void Path_is_the_fold_of_the_player_moves_in_the_log()
    {
        var s = CompletedFourSteps();
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "перенос", CellId: "c1"));
        s.Roll("Вася").Start("Вася").NextRandom(2, 2).Complete("Вася", Difficulty.Hard);

        var expected = s.Log.OfType<PlayerMoved>()
            .Where(e => e.PlayerId == s.PlayerId("Вася"))
            .Aggregate(PlayerPath.At("start"), (path, moved) => path.After(moved));

        Assert.Equal(expected, s.Player("Вася").Path);
        Assert.Equal(Path(["start", "c1", "c2", "c3", "c4"], ["c1", "c2", "c3", "c4", "c5"]), expected);
    }

    /// <summary>Вася on a 10-step map completed a 6-hour game on hard with dice 2 + 2: four steps to c4.</summary>
    private static Scenario CompletedFourSteps()
    {
        var s = Scenario.New()
            .WithMapLength(10)
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror").WithGame("Doom", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася").NextRandom(2, 2).Complete("Вася", Difficulty.Hard);
        ScenarioAssert.Accepted(s);
        return s;
    }
}
