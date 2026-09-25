using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// The first to reach the finish is first (P2; SPEC «Первое место — первый, кто дошёл до финиша»; D-99). When the move
/// of a completion brings the token to the finish, the same command writes <see cref="PlayerFinished"/> after the move,
/// with the order among finishers (1 for the first) and the surplus — the steps that burned at the finish (Q-3). The
/// first holds no finish bonus. With <c>finish.requireApprovalForFirst</c> the first finish is provisional (not frozen)
/// until the proofs of his runs up to the finish are approved; without it the first is frozen at once.
/// </summary>
public class FirstFinisherTests
{
    [Fact]
    public void First_to_reach_finish_is_first()
    {
        // Given nobody finished
        var s = New();
        var vasya = s.PlayerId("Вася");
        Assert.Null(FinishLine.First(s.State));

        // When Вася completes with 3 + 1 from the start: exactly the 4 steps of the map, nothing burns
        var (runId, at) = Complete(s, "Вася", [3, 1]);

        // Then the move to the finish is followed by his finish, order 1, at the time of the completion, surplus 0
        var moved = s.LastEvents<PlayerMoved>().Single();
        Assert.Equal(LinearMap.FinishId, moved.To);
        Assert.Equal(new PlayerFinished(vasya, runId, 1, at, 0), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.True(IndexOf<PlayerFinished>(s) > IndexOf<PlayerMoved>(s), "The finish must follow the move.");

        // And he is first; the finish is provisional (the first finish needs approved proofs)
        Assert.Equal(vasya, FinishLine.First(s.State));
        Assert.Equal(new FinishState(1, runId, at, Frozen: false, Bonus: 0, Surplus: 0, FinishBonusRules.Of(s.Ruleset.Finish), s.Ruleset.Finish.RequireApprovalForFirst), FinishOf(s, "Вася"));
        Assert.Empty(s.LastEvents<PlayerFrozen>());

        // And the completion counted as usual: 4 points, 6 coins, on the finish
        Assert.Equal((4, CoinsPerRun, LinearMap.FinishId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void First_finisher_gets_no_finish_bonus()
    {
        var s = New();

        FinishRun(s, "Вася");

        Assert.Empty(BonusChanges(s));
        Assert.Equal([PointsReason.CompletionRoll], s.LastEvents<PointsChanged>().Select(e => e.Reason));
        Assert.Equal((4, 0), (s.Player("Вася").Points, FinishOf(s, "Вася")!.Bonus));
    }

    [Fact]
    public void Completion_one_step_short_of_the_finish_does_not_finish()
    {
        // 1 + 2 → c3, one cell before the finish
        var s = New();

        Complete(s, "Вася", [1, 2]);

        Assert.Equal("c3", s.Player("Вася").CellId);
        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Null(FinishOf(s, "Вася"));
        Assert.Null(FinishLine.First(s.State));
    }

    [Fact]
    public void Overshooting_the_finish_keeps_the_burned_steps_as_the_surplus()
    {
        // 4 + 4 = 8 steps on a map of 4: 4 burn (D-47) and are the surplus (Q-3); the points stay
        var s = New();
        var vasya = s.PlayerId("Вася");

        var (runId, at) = Complete(s, "Вася", [4, 4]);

        Assert.Equal(new PlayerFinished(vasya, runId, 1, at, 4), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.Equal(4, FinishOf(s, "Вася")!.Surplus);
        Assert.Equal((8, LinearMap.FinishId), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Finish_after_several_runs_is_linked_to_the_run_that_reached_it()
    {
        // 1 + 1 → c2 (no finish), then 2 + 2 from c2: two steps to the finish, two burn
        var s = New();
        var vasya = s.PlayerId("Вася");
        var (first, _) = Complete(s, "Вася", [1, 1]);
        Assert.Null(FinishOf(s, "Вася"));

        var (second, at) = Complete(s, "Вася", [2, 2]);

        Assert.Equal(new PlayerFinished(vasya, second, 1, at, 2), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.Equal(second, FinishOf(s, "Вася")!.RunId);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Second_to_reach_the_finish_is_not_first()
    {
        var s = New();
        var vasya = s.PlayerId("Вася");
        FinishRun(s, "Вася");

        var (runId, at) = Complete(s, "Петя", [3, 1]);

        Assert.Equal(new PlayerFinished(s.PlayerId("Петя"), runId, 2, at, 0), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.Equal(vasya, FinishLine.First(s.State));
        Assert.Equal(1, FinishOf(s, "Вася")!.Order);
    }

    [Fact]
    public void First_place_goes_by_the_finish_not_by_points()
    {
        // Петя has far more points, but Вася reaches the finish first
        var s = New();
        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Бонус за конкурс", PointsDelta: 100));
        ScenarioAssert.Accepted(s);
        Complete(s, "Петя", [1, 1]);

        FinishRun(s, "Вася");

        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
    }

    [Fact]
    public void Admin_move_onto_the_cell_before_the_finish_does_not_finish()
    {
        // Only a run's move finishes (D-99): the admin's transfer to c3 writes no finish
        var s = New();

        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "c3"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Null(FinishLine.First(s.State));
    }

    // ---- finish.requireApprovalForFirst = false ----

    [Fact]
    public void Without_required_approval_the_first_is_frozen_at_once()
    {
        var s = New(ruleset: r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } });
        var vasya = s.PlayerId("Вася");

        var (runId, at) = Complete(s, "Вася", [3, 1]);

        // The freeze follows the finish in the same command
        Assert.Equal(new PlayerFrozen(vasya), Assert.Single(s.LastEvents<PlayerFrozen>()));
        Assert.True(IndexOf<PlayerFrozen>(s) > IndexOf<PlayerFinished>(s), "The freeze must follow the finish.");
        Assert.Equal(new FinishState(1, runId, at, Frozen: true, Bonus: 0, Surplus: 0, FinishBonusRules.Of(s.Ruleset.Finish), s.Ruleset.Finish.RequireApprovalForFirst), FinishOf(s, "Вася"));

        // The run that reached the finish still counts: its points and coins came before the freeze
        Assert.Equal((4, CoinsPerRun), (s.Player("Вася").Points, s.Player("Вася").Coins));
    }

    [Fact]
    public void Without_required_approval_a_later_finisher_is_not_frozen()
    {
        var s = New(ruleset: r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } });
        FinishRun(s, "Вася");

        FinishRun(s, "Петя");

        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.False(FinishOf(s, "Петя")!.Frozen);
    }

    [Fact]
    public void Without_required_approval_the_later_approval_does_not_freeze_again()
    {
        var s = New(ruleset: r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } });
        var runId = FinishRun(s, "Вася");

        Approve(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());
        Assert.True(FinishOf(s, "Вася")!.Frozen);
    }

    // ---- Replay ----

    [Fact]
    public void Replaying_the_log_gives_the_finish_state()
    {
        var s = New();
        var first = FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        Approve(s, first);
        ScenarioAssert.Accepted(s);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(SeasonEngine.Replay(s.Log)));
    }

    [Fact]
    public void Nobody_is_first_in_a_new_season_or_after_runs_short_of_the_finish()
    {
        var s = New();
        Assert.Null(FinishLine.First(s.State));

        Complete(s, "Вася", [1, 1]);
        Complete(s, "Петя", [1, 2]);

        Assert.Null(FinishLine.First(s.State));
        Assert.All(s.State.Players.Values, p => Assert.Null(p.Finish));
        Assert.DoesNotContain(s.Log, e => e is PlayerFinished);
        Assert.Equal(RunStatus.Completed, s.State.Runs.Values.First().Status);
    }
}
