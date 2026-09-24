using GameEvent.Engine.Map;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// Trigger points of a move (SPEC «Движение», CONTENT.md triggers <c>moveStep</c>, <c>pass</c>, <c>stop</c>, D-90):
/// each entered cell gets a MoveStep, then Pass, or Stop for the last one. A transfer's destination does not fire,
/// and a move that entered no cell stops nowhere.
/// </summary>
public class VisitTests
{
    private static readonly Guid s_player = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static PlayerMoved Moved(string from, int steps, params string[] path) =>
        new(s_player, from, path.Length == 0 ? from : path[^1], steps, [.. path], MoveReason.CompletionRoll, RunId: null);

    [Fact]
    public void Each_cell_gets_a_move_step_then_pass_and_the_last_one_a_stop()
    {
        var visits = Movement.Visits(Moved("start", 3, "c1", "c2", "c3"));

        Assert.Equal(
            [
                new CellVisit("c1", CellVisitKind.MoveStep),
                new CellVisit("c1", CellVisitKind.Pass),
                new CellVisit("c2", CellVisitKind.MoveStep),
                new CellVisit("c2", CellVisitKind.Pass),
                new CellVisit("c3", CellVisitKind.MoveStep),
                new CellVisit("c3", CellVisitKind.Stop),
            ],
            visits);
    }

    [Fact]
    public void A_single_step_is_a_move_step_and_a_stop()
    {
        Assert.Equal(
            [new CellVisit("c4", CellVisitKind.MoveStep), new CellVisit("c4", CellVisitKind.Stop)],
            Movement.Visits(Moved("c3", 1, "c4")));
    }

    [Fact]
    public void Overshooting_the_finish_stops_on_the_finish()
    {
        // D-47: Steps is the full dice sum, but only entered cells fire
        var visits = Movement.Visits(Moved("c3", 9, "c4", "finish"));

        Assert.Equal(
            [
                new CellVisit("c4", CellVisitKind.MoveStep),
                new CellVisit("c4", CellVisitKind.Pass),
                new CellVisit("finish", CellVisitKind.MoveStep),
                new CellVisit("finish", CellVisitKind.Stop),
            ],
            visits);
    }

    [Fact]
    public void Moving_back_fires_the_same_kinds()
    {
        var visits = Movement.Visits(Moved("c3", -2, "c2", "c1"));

        Assert.Equal(
            [
                new CellVisit("c2", CellVisitKind.MoveStep),
                new CellVisit("c2", CellVisitKind.Pass),
                new CellVisit("c1", CellVisitKind.MoveStep),
                new CellVisit("c1", CellVisitKind.Stop),
            ],
            visits);
    }

    [Fact]
    public void Moving_back_clamped_at_start_stops_on_the_start()
    {
        var visits = Movement.Visits(Moved("c1", -5, "start"));

        Assert.Equal(
            [new CellVisit("start", CellVisitKind.MoveStep), new CellVisit("start", CellVisitKind.Stop)],
            visits);
    }

    [Theory]
    [InlineData(MoveReason.AdminAdjustment)]
    [InlineData(MoveReason.StartingCell)]
    public void A_transfer_has_no_trigger_points(MoveReason reason)
    {
        // SPEC «Движение»: the destination of a transfer does not fire
        var transfer = new PlayerMoved(s_player, "c2", "c7", 0, ["c7"], reason, RunId: null);

        Assert.Empty(Movement.Visits(transfer));
    }

    [Fact]
    public void A_move_that_entered_no_cell_stops_nowhere()
    {
        // Blocked at the finish or the start: nothing entered, so nothing fires, not even the cell it stands on
        Assert.Empty(Movement.Visits(Moved("finish", 4)));
        Assert.Empty(Movement.Visits(Moved("start", -3)));
    }
}
