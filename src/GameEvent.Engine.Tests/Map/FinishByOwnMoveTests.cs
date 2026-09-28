using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Map;

/// <summary>
/// The finish is reached only by a run's own move (SPEC «Движение»; «Проверка на дыры» «Затолкать соперника на финиш
/// чужим предметом»; D-322): any other move forward that would reach the finish — a correction now, pushes of effects and
/// items later — stops on the cell it passed just before the finish, the previous cell of its own path (on a graph the
/// push follows the default branches); from the cell before the finish it moves nothing. A teleport never leads to the
/// finish (the map check), an admin never transfers onto it (<c>map.transferToFinish</c>). Both map modes.
/// </summary>
public class FinishByOwnMoveTests
{
    /// <summary>start → a → f → b → finish, f → c → finish; b is the default branch out of f.</summary>
    private static MapGraph Fork() =>
        MapBuilder.New().Path("start", "a", "f", "b", "finish").Path("f", "c", "finish").Build();

    // ---- Movement.Push ----

    [Fact]
    public void Push_that_would_reach_the_finish_stops_on_the_cell_before_it()
    {
        Assert.Equal(["f", "b"], Movement.Push(Fork(), "a", 5));
        Assert.Equal(["c1", "c2", "c3", "c4"], Movement.Push(LinearMap.Generate(5), "start", 9));
    }

    [Fact]
    public void Push_from_the_cell_before_the_finish_moves_nothing()
    {
        Assert.Empty(Movement.Push(Fork(), "b", 3));
        Assert.Empty(Movement.Push(LinearMap.Generate(5), "c4", 1));
    }

    [Fact]
    public void Push_short_of_the_finish_takes_all_its_steps_along_the_default_branch()
    {
        Assert.Equal(["a", "f"], Movement.Push(Fork(), "start", 2));
        Assert.Equal(["a", "f", "b"], Movement.Push(Fork(), "start", 3));
    }

    // ---- A correction on a graph ----

    [Fact]
    public void Graph_correction_forward_stops_on_the_cell_before_the_finish_along_the_default_branch()
    {
        // Вася threw 1 → a; 3 → 9 hours adds two dice 4 + 4: a → f → b → finish would finish him
        var s = Scenario.New().WithMap(Fork()).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1).Complete("Вася");
        var runId = s.State.Runs.Values.Single().RunId;

        s.NextRandom(4, 4).Act(new CorrectRunHours(runId, 9, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(("a", "b", MoveReason.RunCorrection), (moved.From, moved.To, moved.Reason));
        Assert.Equal(["f", "b"], moved.Path);
        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Null(s.Player("Вася").Finish);
        Assert.Equal(9, s.Player("Вася").Points);
    }

    [Fact]
    public void Graph_admin_transfer_onto_the_finish_is_rejected()
    {
        var s = Scenario.New().WithMap(Fork()).WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new AdjustPlayer(x.PlayerId("Вася"), "Перенос", CellId: "finish")), RejectionCodes.TransferToFinish);
    }

    [Fact]
    public void Own_move_finishes_on_a_graph()
    {
        var s = Scenario.New().WithMap(Fork()).WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");

        // 4: a, f — the fork asks for the branch — then b, finish: the steps after the choice are the run's own move
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(4).Complete("Вася").ChooseBranch("Вася", "b");

        ScenarioAssert.Accepted(s);
        Assert.Equal(MoveReason.CompletionRoll, s.LastEvents<PlayerMoved>().Single().Reason);
        Assert.Equal("finish", s.Player("Вася").CellId);
        Assert.NotNull(s.Player("Вася").Finish);
    }
}
