using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Proofs.ProofSetup;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// The admin's queue of runs to check (SE6; SPEC «Финиш прохождения на проверке поднимается в начало очереди пруфов»;
/// D-98). <see cref="ProofReviewOrder.Order"/> lists completed runs that are neither approved nor rejected, whether a
/// proof was sent or not: runs whose move brought the player to the finish first, then by completion time, ties by run
/// id. Runs being played, dropped, tech-rerolled, approved or rejected are not in the queue. The map here is 8 steps:
/// two d4 showing 4 and 4 reach the finish.
/// </summary>
public class ProofReviewOrderTests
{
    private static Scenario Season()
    {
        var s = Scenario.New().WithMapLength(8).WithCategory("Horror");
        foreach (var title in new[] { "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma" })
        {
            s.WithGame(title, 6, "Horror");
        }

        return s.WithPlayers("Вася", "Петя", "Маша");
    }

    [Fact]
    public void Season_without_completed_runs_has_an_empty_queue()
    {
        var s = Season();
        s.Roll("Вася").Start("Вася");

        Assert.Empty(ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Completed_runs_wait_in_the_order_they_were_completed()
    {
        // Петя starts first (his run id is lower), but Вася completes first
        var s = Season();
        s.Roll("Петя").Start("Петя");
        var petya = s.Player("Петя").ActiveRunId!.Value;
        var vasya = CompleteRun(s, "Вася", [1, 1]);
        s.NextRandom(1, 1).Complete("Петя");
        ScenarioAssert.Accepted(s);

        Assert.True(petya.CompareTo(vasya) < 0, "The test needs Петя's run id to be the lower one.");
        Assert.Equal([vasya, petya], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Runs_with_and_without_a_proof_are_both_in_the_queue()
    {
        var s = Season();
        var first = CompleteRun(s, "Вася", [1, 1]);
        var second = CompleteRun(s, "Петя", [1, 1]);
        Submit(s, "Петя", second, [Link]);
        ScenarioAssert.Accepted(s);

        Assert.Equal([first, second], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void New_proof_does_not_move_a_run_in_the_queue()
    {
        // The queue goes by completion, not by when the proof came
        var s = Season();
        var first = CompleteRun(s, "Вася", [1, 1]);
        var second = CompleteRun(s, "Петя", [1, 1]);
        Submit(s, "Петя", second, [Link]);
        s.Advance(TimeSpan.FromHours(1));
        Submit(s, "Вася", first, [Link]);
        ScenarioAssert.Accepted(s);

        Assert.Equal([first, second], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Run_that_reached_the_finish_goes_on_top()
    {
        // Вася and Петя complete first; Маша's 4 + 4 later brings her to the finish
        var s = Season();
        var vasya = CompleteRun(s, "Вася", [1, 1]);
        var petya = CompleteRun(s, "Петя", [2, 1]);
        var masha = CompleteRun(s, "Маша", [4, 4]);

        Assert.Equal(LinearMap.FinishId, s.Player("Маша").CellId);
        Assert.True(s.State.Runs[masha].ReachedFinish);
        Assert.False(s.State.Runs[vasya].ReachedFinish);
        Assert.Equal([masha, vasya, petya], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Run_that_only_moved_the_player_forward_did_not_reach_the_finish()
    {
        // 4 + 3 = 7 of 8 steps: one short of the finish
        var s = Season();
        var runId = CompleteRun(s, "Маша", [4, 3]);

        Assert.Equal("c7", s.Player("Маша").CellId);
        Assert.False(s.State.Runs[runId].ReachedFinish);
    }

    [Fact]
    public void Run_that_reached_the_finish_leaves_the_top_once_approved()
    {
        var s = Season();
        var vasya = CompleteRun(s, "Вася", [1, 1]);
        var masha = CompleteRun(s, "Маша", [4, 4]);
        Submit(s, "Маша", masha, [Link]);
        Approve(s, masha);
        ScenarioAssert.Accepted(s);

        Assert.Equal([vasya], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Approved_and_rejected_runs_leave_the_queue()
    {
        var s = Season();
        var approved = CompleteRun(s, "Вася", [1, 1]);
        var rejected = CompleteRun(s, "Петя", [1, 1]);
        var waiting = CompleteRun(s, "Маша", [1, 1]);
        Approve(s, approved, comment: "Видел на стриме");
        ScenarioAssert.Accepted(s);
        Reject(s, rejected);
        ScenarioAssert.Accepted(s);

        Assert.Equal([waiting], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Runs_being_played_dropped_or_tech_rerolled_are_not_in_the_queue()
    {
        var s = Season();
        var completed = CompleteRun(s, "Вася", [1, 1]);
        s.Roll("Вася").Start("Вася");
        DroppedRun(s, "Петя");
        s.Roll("Маша").Start("Маша");
        s.Act(new TechReroll(s.PlayerId("Маша"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);

        Assert.Equal([completed], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Runs_completed_at_the_same_time_go_by_run_id()
    {
        // The clock does not move between the two completions
        var s = Season();
        s.Roll("Петя").Start("Петя");
        var petya = s.Player("Петя").ActiveRunId!.Value;
        s.Roll("Вася").Start("Вася");
        var vasya = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(1, 1).Complete("Вася");
        s.NextRandom(1, 1).Complete("Петя");
        ScenarioAssert.Accepted(s);

        Assert.Equal(s.State.Runs[vasya].CompletedAt, s.State.Runs[petya].CompletedAt);
        Assert.Equal([petya, vasya], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Several_finishes_go_on_top_by_completion_time()
    {
        // Маша reaches the finish, then Вася does; Петя's run is earlier than both but did not finish
        var s = Season();
        var petya = CompleteRun(s, "Петя", [1, 1]);
        var masha = CompleteRun(s, "Маша", [4, 4]);
        var vasya = CompleteRun(s, "Вася", [4, 4]);

        Assert.Equal([masha, vasya, petya], ProofReviewOrder.Order(s.State));
    }

    [Fact]
    public void Queue_is_read_from_the_state_and_changes_nothing()
    {
        var s = Season();
        CompleteRun(s, "Вася", [1, 1]);
        var before = s.State;

        var first = ProofReviewOrder.Order(s.State);
        var second = ProofReviewOrder.Order(SeasonEngine.Replay(s.Log));

        Assert.Equal(first, second);
        Assert.Equal(before, s.State);
    }
}
