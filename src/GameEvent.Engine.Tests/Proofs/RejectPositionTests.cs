using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// A reject never improves the position (the owner's decision on D-303, 2026-09-28; D-321). If the player has not moved
/// since the rejected run's move, they go back to the cell before that move — a teleport the move stopped on included.
/// If they moved since, they go back along their own path by as many cells as that move brought them closer to the finish
/// (cells to the finish, teleport included). If the move took them further from the finish (a snake), the position stays
/// and only the points go. A walk back that would still end closer to the finish than where they stand moves nothing.
/// Games of 3 hours give one d4 (hoursPerDie 3, normal d4).
/// </summary>
public class RejectPositionTests
{
    private const string Comment = "Фейковое прохождение";

    private static Scenario Pool(MapGraph? map = null)
    {
        var s = Scenario.New();
        if (map is not null)
        {
            s.WithMap(map);
        }

        return s.WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror").WithGame("Kuon", 3, "Horror")
            .WithPlayers("Вася", "Петя");
    }

    /// <summary>Вася rolls <paramref name="title"/>, plays it and throws <paramref name="die"/>; returns the run.</summary>
    private static Guid Throw(Scenario s, string title, int die)
    {
        s.RollTitle("Вася", title).Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(die).Complete("Вася");
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    private static void Reject(Scenario s, Guid runId)
    {
        s.Act(new RejectProof(runId, Comment));
        ScenarioAssert.Accepted(s);
    }

    /// <summary>
    /// start → a → t → b → c → d → e → g → h → finish, t a shortcut to e. Cells to the finish: start 9, a 8, t 7, e 3,
    /// g 2, h 1.
    /// </summary>
    private static MapGraph Shortcut() =>
        MapBuilder.New().Path("start", "a", "t", "b", "c", "d", "e", "g", "h", "finish").Teleport("t", to: "e").Build();

    // ---- Graph: a fake run for a shortcut ----

    [Fact]
    public void Fake_run_for_a_shortcut_returns_the_player_to_the_cell_before_its_move()
    {
        // Given Вася threw 2 onto the shortcut t and was carried to e, and has not moved since
        var s = Pool(Shortcut());
        var run = Throw(s, "Silent Hill", 2);
        Assert.Equal("e", s.Player("Вася").CellId);

        // When the run is rejected
        Reject(s, run);

        // Then he is back where the move began, not 2 cells back from e
        var vasya = s.PlayerId("Вася");
        Assert.Equal(
            new PlayerMoved(vasya, "e", "start", 0, ["start"], MoveReason.ProofRejected, run),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(("start", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
        Assert.Equal("start", s.Player("Вася").Path.Current);
    }

    [Fact]
    public void Fake_shortcut_run_after_a_move_on_goes_back_by_what_it_gained_teleport_included()
    {
        // Given the shortcut run brought Вася from start (9 to the finish) to e (3): 6 closer; then he moved on to g
        var s = Pool(Shortcut());
        var fake = Throw(s, "Silent Hill", 2);
        Throw(s, "Fatal Frame", 1);
        Assert.Equal("g", s.Player("Вася").CellId);

        // When the shortcut run is rejected
        Reject(s, fake);

        // Then he goes 6 cells back along his path: g → e, then along the main entries e → d → c → b → t → a
        var vasya = s.PlayerId("Вася");
        Assert.Equal(
            new PlayerMoved(vasya, "g", "a", -6, ["e", "d", "c", "b", "t", "a"], MoveReason.ProofRejected, fake),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(("a", 1), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    // ---- Graph: a snake ----

    /// <summary>start → a → b → c → s → d → g → finish, s a snake to a. Cells to the finish: start 7, a 6, b 5, c 4.</summary>
    private static MapGraph Snake() =>
        MapBuilder.New().Path("start", "a", "b", "c", "s", "d", "g", "finish").Teleport("s", to: "a").Build();

    [Fact]
    public void Reject_of_a_snake_move_keeps_the_position_and_takes_only_the_points()
    {
        // Given Вася stood on b (5 to the finish), threw 2 onto the snake and slid to a (6): the move took him further
        var s = Pool(Snake());
        Throw(s, "Silent Hill", 2);
        var snake = Throw(s, "Fatal Frame", 2);
        Assert.Equal(("a", 4), (s.Player("Вася").CellId, s.Player("Вася").Points));

        // When that run is rejected
        Reject(s, snake);

        // Then the position stays — going back to b would improve it — and only the points go
        var vasya = s.PlayerId("Вася");
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(new PointsChanged(vasya, -2, PointsReason.ProofRejected, snake), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(("a", 2), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Reject_of_a_snake_move_after_a_move_on_keeps_the_position()
    {
        var s = Pool(Snake());
        Throw(s, "Silent Hill", 2);
        var snake = Throw(s, "Fatal Frame", 2);
        Throw(s, "Siren", 1);
        Assert.Equal("b", s.Player("Вася").CellId);

        Reject(s, snake);

        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(("b", 3), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    // ---- Graph: branches of different length ----

    /// <summary>
    /// start → f → l1 → l2 → l3 → j → finish with a short branch f → j (not the default). Cells to the finish: start 3,
    /// f 2 (by the short branch), l1 4, j 1.
    /// </summary>
    private static MapGraph Branches() =>
        MapBuilder.New().Path("start", "f", "l1", "l2", "l3", "j", "finish").Path("f", "j").Build();

    [Fact]
    public void Walk_back_that_would_end_closer_to_the_finish_keeps_the_position()
    {
        // Given Вася reached the fork f (run 1: 3 → 2 to the finish, 1 closer) and then took the long branch to l1 (4)
        var s = Pool(Branches());
        var first = Throw(s, "Silent Hill", 1);
        Throw(s, "Fatal Frame", 1);
        s.ChooseBranch("Вася", "l1");
        ScenarioAssert.Accepted(s);
        Assert.Equal("l1", s.Player("Вася").CellId);

        // When run 1 is rejected: 1 cell back along his path is the fork, 2 from the finish — closer than l1
        Reject(s, first);

        // Then he stays: a reject never improves the position
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(("l1", 1), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Reject_of_a_move_into_the_longer_branch_keeps_the_position()
    {
        // The move f → l1 took Вася from 2 to 4 cells to the finish: like a snake, going back would improve the position
        var s = Pool(Branches());
        Throw(s, "Silent Hill", 1);
        var longer = Throw(s, "Fatal Frame", 1);
        s.ChooseBranch("Вася", "l1");
        ScenarioAssert.Accepted(s);

        Reject(s, longer);

        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal(("l1", 1), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    // ---- Graph: a finisher (SPEC «Первое место»: a reject of any run up to the finish recalculates the position) ----

    [Fact]
    public void Reject_of_an_earlier_shortcut_run_of_a_finisher_takes_its_gain_beyond_the_surplus()
    {
        // Given the shortcut run brought Вася 6 closer (start → e); the next run threw 4 from e: 3 cells to the finish,
        // 1 step burned (surplus 1)
        var s = Pool(Shortcut());
        var fake = Throw(s, "Silent Hill", 2);
        Throw(s, "Fatal Frame", 4);
        Assert.NotNull(s.Player("Вася").Finish);
        Assert.Equal(1, s.Player("Вася").Finish!.Surplus);

        // When the shortcut run is rejected
        Reject(s, fake);

        // Then 6 > 1: the finish is revoked and he goes back 5 along his path: finish → h → g → e, then d, c
        var vasya = s.PlayerId("Вася");
        Assert.Contains(new PlayerFinishRevoked(vasya, s.State.Runs.Values.Single(r => r.RunId != fake && r.PlayerId == vasya).RunId), s.Last.Events);
        Assert.Equal(
            new PlayerMoved(vasya, "finish", "c", -5, ["h", "g", "e", "d", "c"], MoveReason.ProofRejected, fake),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Null(s.Player("Вася").Finish);
        Assert.Equal(("c", 4), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    // ---- Linear map: the same rule, where the gain is the cells walked ----

    [Fact]
    public void Linear_fake_run_not_moved_since_returns_the_player_to_the_cell_before_its_move()
    {
        var s = Pool();
        Throw(s, "Silent Hill", 3);
        var fake = Throw(s, "Fatal Frame", 4);
        Assert.Equal("c7", s.Player("Вася").CellId);

        Reject(s, fake);

        Assert.Equal(
            new PlayerMoved(s.PlayerId("Вася"), "c7", "c3", -4, ["c6", "c5", "c4", "c3"], MoveReason.ProofRejected, fake),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(("c3", 3), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Linear_fake_run_after_a_move_on_goes_back_by_what_it_gained()
    {
        var s = Pool();
        var fake = Throw(s, "Silent Hill", 3);
        Throw(s, "Fatal Frame", 2);
        Assert.Equal("c5", s.Player("Вася").CellId);

        Reject(s, fake);

        Assert.Equal(
            new PlayerMoved(s.PlayerId("Вася"), "c5", "c2", -3, ["c4", "c3", "c2"], MoveReason.ProofRejected, fake),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(("c2", 2), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Linear_reject_after_an_admin_transfer_goes_back_by_the_gain_from_the_new_cell()
    {
        // An admin transfer is a move since: the run's gain comes off where the player stands now
        var s = Pool();
        var fake = Throw(s, "Silent Hill", 3);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "c10"));
        ScenarioAssert.Accepted(s);

        Reject(s, fake);

        Assert.Equal(("c7", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    // ---- What «moved since» means ----

    [Fact]
    public void Own_hours_correction_after_the_move_is_not_a_move_since()
    {
        // The run's own correction right after its move belongs to it: the reject takes the player to where it began
        var s = Pool();
        var run = Throw(s, "Silent Hill", 3);
        s.NextRandom(2).Act(new CorrectRunHours(run, 6, "Часы по HLTB"));
        ScenarioAssert.Accepted(s);
        Assert.Equal("c5", s.Player("Вася").CellId);

        Reject(s, run);

        Assert.Equal(("start", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Drop_penalty_after_the_move_is_a_move_since()
    {
        // B brought Вася c3 → c7 (4 closer); a drop pushed him back to c5; the reject of B takes 4 from there
        var s = Pool();
        Throw(s, "Silent Hill", 3);
        var b = Throw(s, "Fatal Frame", 4);
        s.RollTitle("Вася", "Siren").Start("Вася").NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        Assert.Equal("c5", s.Player("Вася").CellId);

        Reject(s, b);

        Assert.Equal("c1", s.Player("Вася").CellId);
    }

    [Fact]
    public void Correction_of_another_run_after_the_move_is_a_move_since()
    {
        // B moved last (c3 → c7); then A's hours went up by a die of 2 (c7 → c9): the reject of B takes 4 from c9
        var s = Pool();
        var a = Throw(s, "Silent Hill", 3);
        var b = Throw(s, "Fatal Frame", 4);
        s.NextRandom(2).Act(new CorrectRunHours(a, 6, "Часы по HLTB"));
        ScenarioAssert.Accepted(s);
        Assert.Equal("c9", s.Player("Вася").CellId);

        Reject(s, b);

        Assert.Equal("c5", s.Player("Вася").CellId);
    }

    [Fact]
    public void Reject_after_the_cell_before_the_move_left_the_map_goes_back_by_the_gain()
    {
        // Вася: 1 → a, then 3 → d (3 closer); a new map drops a (D-308): the reject still takes the 3 cells back
        var s = Pool(MapBuilder.New().Path("start", "a", "b", "c", "d", "e", "g", "finish").Build());
        Throw(s, "Silent Hill", 1);
        var run = Throw(s, "Fatal Frame", 3);
        s.WithMap(MapBuilder.New().Path("start", "b", "c", "d", "e", "g", "finish").Build());
        Assert.Equal("d", s.Player("Вася").CellId);

        Reject(s, run);

        Assert.Equal("start", s.Player("Вася").CellId);
    }
}
