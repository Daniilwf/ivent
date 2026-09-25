using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// Details of D-99 settled in the C9a review: the first's own free-mode games stay «completed» for him; runs record at
/// completion whether they came after the finish (<see cref="RunCompleted.AfterFinish"/>) and in free mode
/// (<see cref="RunCompleted.FreeMode"/>), and a revoke turns later runs back into runs up to a future finish; the finish
/// events of a completion come after its coins and difficulty event and before the review; a frozen first gets no new
/// difficulty event; the admin cannot move a finisher's token; bonuses follow the rules in force at each recalculation;
/// a frozen first's reroll is paid «free mode».
/// </summary>
public class FinishDetailsTests
{
    // ---- 1. The first's free-mode games (D-16) ----

    [Fact]
    public void Game_the_first_completed_in_free_mode_is_not_rolled_by_himself_again()
    {
        // Two games: A finished Вася (completed in the season), B he completed in free mode — nothing is left for him
        var s = New(players: 2, games: 2);
        FrozenFirst(s, "Вася");
        Complete(s, "Вася", [2, 2]);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Game_the_first_completed_in_free_mode_is_a_completed_miss_for_himself_but_offered_to_others()
    {
        // Three games: A (finish), B (free mode), C left. Вася only ever gets C; Петя gets B or C, never A
        var s = New(players: 2, games: 3);
        var finishing = FinishRun(s, "Вася");
        var (freeRun, _) = Complete(s, "Вася", [2, 2]);
        var (gameA, gameB) = (s.State.Runs[finishing].GameId, s.State.Runs[freeRun].GameId);
        var petyaGotB = false;

        for (var i = 0; i < 20; i++)
        {
            s.Roll("Вася");
            var mine = s.LastEvents<GameRolled>().Single();
            Assert.NotEqual(gameB, mine.GameId);
            Assert.All(mine.Misses.Where(m => m.GameId == gameB), m => Assert.Equal(new RollMiss(gameB, RollMissReason.CompletedInSeason, s.PlayerId("Вася")), m));
            s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сброс", DiscardOffer: true));
            ScenarioAssert.Accepted(s);

            s.Roll("Петя");
            var his = s.LastEvents<GameRolled>().Single();
            Assert.NotEqual(gameA, his.GameId);
            Assert.DoesNotContain(his.Misses, m => m.GameId == gameB && m.Reason == RollMissReason.CompletedInSeason);
            petyaGotB |= his.GameId == gameB;
            s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Сброс", DiscardOffer: true));
            ScenarioAssert.Accepted(s);
        }

        Assert.True(petyaGotB, "B was never offered to Петя in 20 rolls.");
    }

    // ---- 2. AfterFinish and FreeMode at completion ----

    [Fact]
    public void Finishing_run_is_neither_after_the_finish_nor_in_free_mode()
    {
        var s = New();

        var runId = FinishRun(s, "Вася");

        var completed = s.LastEvents<RunCompleted>().Single();
        Assert.Equal((false, false), (completed.AfterFinish, completed.FreeMode));
        Assert.Equal((false, false), (s.State.Runs[runId].AfterFinish, s.State.Runs[runId].FreeMode));
    }

    [Fact]
    public void Later_finisher_run_after_the_finish_is_after_finish_but_not_free_mode()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");

        var (later, _) = Complete(s, "Петя", [2, 2]);

        var completed = s.LastEvents<RunCompleted>().Single();
        Assert.Equal((true, false), (completed.AfterFinish, completed.FreeMode));
        Assert.Equal((true, false), (s.State.Runs[later].AfterFinish, s.State.Runs[later].FreeMode));
    }

    [Fact]
    public void First_run_after_the_finish_is_after_finish_and_free_mode()
    {
        var s = New();
        FinishRun(s, "Вася");

        var (later, _) = Complete(s, "Вася", [2, 2]);

        var completed = s.LastEvents<RunCompleted>().Single();
        Assert.Equal((true, true), (completed.AfterFinish, completed.FreeMode));
        Assert.Equal((true, true), (s.State.Runs[later].AfterFinish, s.State.Runs[later].FreeMode));
    }

    [Fact]
    public void Run_of_a_player_who_has_not_finished_is_neither()
    {
        var s = New();
        FinishRun(s, "Вася");

        Complete(s, "Петя", [1, 1]);

        var completed = s.LastEvents<RunCompleted>().Single();
        Assert.Equal((false, false), (completed.AfterFinish, completed.FreeMode));
    }

    // ---- 3. A revoke turns later runs back into runs before a finish ----

    [Fact]
    public void Revoke_clears_after_finish_of_later_runs_but_keeps_free_mode()
    {
        var s = New();
        var finishing = FinishRun(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);

        Reject(s, finishing);

        ScenarioAssert.Accepted(s);
        Assert.Null(FinishOf(s, "Вася"));
        Assert.Equal((false, true), (s.State.Runs[later].AfterFinish, s.State.Runs[later].FreeMode));
    }

    [Fact]
    public void After_a_revoke_the_next_freeze_waits_for_the_formerly_later_runs()
    {
        // Вася finishes (F), completes L after it, F is rejected; he finishes again with G. L now counts as a run up to
        // the finish: approving G alone does not freeze him, approving L too does
        var s = New();
        var finishing = FinishRun(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);
        Reject(s, finishing);
        ScenarioAssert.Accepted(s);
        var again = FinishRun(s, "Вася");
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));

        Approve(s, again);
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());

        Approve(s, later);
        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFrozen(s.PlayerId("Вася")), s.Last.Events[^1]);
    }

    [Fact]
    public void After_a_revoke_the_formerly_later_runs_go_on_top_of_the_queue_with_the_new_finish()
    {
        var s = New();
        var finishing = FinishRun(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);
        var (petyaRun, _) = Complete(s, "Петя", [1, 1]);
        Reject(s, finishing);
        ScenarioAssert.Accepted(s);

        var again = FinishRun(s, "Вася");

        Assert.Equal([later, again, petyaRun], ProofReviewOrder.Order(s.State));
    }

    // ---- 5. Order of a completion that finishes ----

    [Fact]
    public void Finish_events_follow_the_coins_and_the_difficulty_event_and_precede_the_review()
    {
        // Петя finishes second on «выше сложной» with a review
        var s = New();
        FinishRun(s, "Вася");
        s.Roll("Петя").Start("Петя");
        s.NextRandom(3, 1).Complete("Петя", Difficulty.Extreme, review: new RunReview(9, "Отлично"));

        Assert.Equal(
            [
                typeof(RunCompleted), typeof(CompletionRolled), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged),
                typeof(ManualEffectCreated), typeof(PlayerFinished), typeof(PointsChanged), typeof(RunReviewed),
            ],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(PointsReason.FinishBonus, s.Last.Events.OfType<PointsChanged>().Last().Reason);
    }

    [Fact]
    public void Freeze_at_the_finish_comes_after_the_finish_and_before_the_review()
    {
        var s = New(ruleset: r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } });
        s.Roll("Вася").Start("Вася");

        s.NextRandom(3, 1).Complete("Вася", Difficulty.Extreme, review: new RunReview(7, null));

        Assert.Equal(
            [
                typeof(RunCompleted), typeof(CompletionRolled), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged),
                typeof(ManualEffectCreated), typeof(PlayerFinished), typeof(PlayerFrozen), typeof(RunReviewed),
            ],
            s.Last.Events.Select(e => e.GetType()));
    }

    // ---- 6. Difficulty change of the frozen first ----

    [Fact]
    public void Frozen_first_difficulty_change_resolves_the_pending_event_but_creates_none()
    {
        // Run P on «выше сложной» while provisional: a good event waits. After the freeze P becomes hard, then extreme
        var s = New();
        var vasya = s.PlayerId("Вася");
        var finishing = FinishRun(s, "Вася");
        var (provisional, _) = Complete(s, "Вася", [2, 2], Difficulty.Extreme);
        var effect = Assert.Single(s.State.ManualEffects.Values).EffectId;
        Approve(s, finishing);
        ScenarioAssert.Accepted(s);
        Assert.True(FinishOf(s, "Вася")!.Frozen);

        s.Act(new ChangeRunDifficulty(provisional, Difficulty.Hard, "По пруфу — сложная"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunDifficultyChanged), typeof(ManualEffectResolved)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(
            new ManualEffectResolved(effect, vasya, provisional, ManualEffectOutcome.NotApplicable, "По пруфу — сложная"),
            s.LastEvents<ManualEffectResolved>().Single());
        Assert.Empty(s.State.ManualEffects);

        // And back to «выше сложной»: no new good event for a frozen first
        s.Act(new ChangeRunDifficulty(provisional, Difficulty.Extreme, "Всё-таки выше сложной"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunDifficultyChanged)], s.Last.Events.Select(e => e.GetType()));
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Provisional_first_difficulty_change_up_creates_the_event_as_usual()
    {
        // The boundary: before the freeze the difficulty event is created
        var s = New();
        FinishRun(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2], Difficulty.Hard);

        s.Act(new ChangeRunDifficulty(later, Difficulty.Extreme, "По пруфу — выше сложной"));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<ManualEffectCreated>());
    }

    // ---- 7. The admin cannot move a finisher ----

    [Fact]
    public void Admin_cannot_move_a_finisher_to_another_cell()
    {
        var s = New();
        FinishRun(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new AdjustPlayer(x.PlayerId("Вася"), "Перенос", CellId: "c2")), RejectionCodes.PlayerFinished);
    }

    [Fact]
    public void Admin_cannot_move_a_frozen_first_either()
    {
        var s = New();
        FrozenFirst(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new AdjustPlayer(x.PlayerId("Вася"), "Перенос", CellId: LinearMap.StartId)), RejectionCodes.PlayerFinished);
    }

    [Fact]
    public void Admin_may_adjust_a_finisher_without_a_cell()
    {
        // No cell in the adjustment: points are adjusted, the token stays
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");

        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Бонус за стрим", PointsDelta: 3));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal((14 + 3, LinearMap.FinishId), (s.Player("Петя").Points, s.Player("Петя").CellId));
        Assert.Equal(2, FinishOf(s, "Петя")!.Order);
    }

    [Fact]
    public void Admin_may_adjust_a_finisher_naming_the_cell_he_stands_on()
    {
        // The same cell as now is not a move: accepted, the points are adjusted, no move is written
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");

        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Бонус за стрим", CellId: LinearMap.FinishId, PointsDelta: 3));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal((14 + 3, LinearMap.FinishId), (s.Player("Петя").Points, s.Player("Петя").CellId));
    }

    [Fact]
    public void Admin_may_still_move_a_player_who_has_not_finished()
    {
        var s = New();
        FinishRun(s, "Вася");

        s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Перенос", CellId: "c2"));

        ScenarioAssert.Accepted(s);
        Assert.Equal("c2", s.Player("Петя").CellId);
    }

    // ---- 8. Bonuses follow the rules in force at each recalculation ----

    [Fact]
    public void Bonuses_are_recalculated_by_the_list_in_force_at_the_next_finish()
    {
        // Вася 1, Петя 2 (+10 by [10, 8, 6, 4]); the admin changes the list to [20, 15]; Маша finishes third:
        // Петя (place 2) goes 10 → 20, Маша (place 3) gets 15
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        var (petya, masha) = (s.PlayerId("Петя"), s.PlayerId("Маша"));
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20, 15] } });
        Assert.Equal(10, FinishOf(s, "Петя")!.Bonus);

        FinishRun(s, "Маша");

        Assert.Equal(
            [(petya, 10, PointsReason.FinishBonus), (masha, 15, PointsReason.FinishBonus)],
            BonusChanges(s).OrderBy(x => x.Player));
        Assert.Equal((20, 15), (FinishOf(s, "Петя")!.Bonus, FinishOf(s, "Маша")!.Bonus));
        Assert.Equal((24, 19), (s.Player("Петя").Points, s.Player("Маша").Points));
    }

    [Fact]
    public void Changing_the_list_alone_does_not_move_any_bonus()
    {
        // The recalculation happens when the finishers change, not when the rules do
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");

        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20] } });

        Assert.DoesNotContain(s.Last.Events, e => e is PointsChanged);
        Assert.Equal((10, 14), (FinishOf(s, "Петя")!.Bonus, s.Player("Петя").Points));
    }

    [Fact]
    public void Bonuses_after_a_revoke_follow_the_list_in_force()
    {
        // The list changes to [5] (after it 1); Петя, second, is revoked: Маша moves to place 2 — 8 → 5
        var s = New();
        FinishRun(s, "Вася");
        var petyaRun = FinishRun(s, "Петя");
        FinishRun(s, "Маша");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [5], BonusAfterList = 1 } });

        Reject(s, petyaRun);

        ScenarioAssert.Accepted(s);
        Assert.Contains((s.PlayerId("Маша"), -3, PointsReason.FinishBonusRevoked), BonusChanges(s));
        Assert.Equal(5, FinishOf(s, "Маша")!.Bonus);
    }

    // ---- The frozen first's reroll is paid «free mode» ----

    [Fact]
    public void Frozen_first_reroll_is_paid_in_free_mode()
    {
        var s = New();
        FrozenFirst(s, "Вася");
        s.Roll("Вася");

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(RerollPayment.FreeMode, s.LastEvents<GameRerolled>().Single().Payment);
    }
}
