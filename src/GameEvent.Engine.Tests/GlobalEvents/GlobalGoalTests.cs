using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Coop;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.GlobalEvents.GlobalSetup;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// The goal of a global event (task 5.9, EV19, EV20; SPEC «общая цель с прогресс-баром («вместе наиграйте 50 часов»)»,
/// «Финишировавший первым может вносить вклад в кооп-цели», «Уточнения правил»: «Прогресс общих целей в часах считается по
/// часам HowLongToBeat пройденных игр, а не по заявленному времени»; D-518, D-546 (6), D-717, D-755, Н-16, Н-17, Н-23): a
/// completion in the window <c>[start, end)</c> brings the HowLongToBeat hours of its snapshot (a goal of runs: 1), a coop
/// run once; the first finisher contributes and gets nothing; reached once, its reward queued for everyone in the same
/// command; a reject takes the contribution back, the reward stays and the goal is never reached again; an hours correction
/// moves the contribution by the difference. Written by <c>test-author</c> from the SPEC and the public contract, before the
/// implementation is finished.
/// </summary>
public class GlobalGoalTests
{
    private static GlobalProgressChanged Progress(Scenario s) => Assert.Single(s.LastEvents<GlobalProgressChanged>());

    private static Dictionary<Guid, int> CoinsOf(Scenario s) => s.State.Players.ToDictionary(p => p.Key, p => p.Value.Coins);

    // ---- Hours (EV19) ----

    [Fact]
    public void Progress_counts_the_hours_of_the_game_not_the_declared_time()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));

        var run = s.Completed("Вася", "Silent Hill", estimatedHours: 25);

        Assert.Equal(new GlobalProgressChanged(launched.GlobalEventId, run, GlobalProgressReason.Completed, 10m, 10m), Progress(s));
        Assert.Equal([new GlobalContribution(run, 10m)], s.Global(launched.GlobalEventId).Contributions);
        Assert.Equal(10m, s.Global(launched.GlobalEventId).Progress);
    }

    [Fact]
    public void The_hours_of_a_game_without_hours_in_the_pool_are_the_estimate_its_dice_were_thrown_by()
    {
        // D-755: then the estimate is the run's hours
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));

        s.Completed("Вася", "Без часов", estimatedHours: 7);

        Assert.Equal(7m, Progress(s).Delta);
        Assert.Equal(7m, s.Global(launched.GlobalEventId).Progress);
    }

    [Fact]
    public void The_hours_are_those_of_the_snapshot_not_of_the_pool_changed_after_the_roll()
    {
        var s = Season();
        s.Launch("banner", 72, HoursGoal(50));
        s.Playing("Вася", "Silent Hill");
        s.ChangePoolHours("Silent Hill", 30);

        s.CompleteOnes("Вася", 10);

        Assert.Equal(10m, Progress(s).Delta);
    }

    [Fact]
    public void Completions_add_up()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));

        s.Completed("Вася", "Silent Hill");
        s.Completed("Петя", "Alan Wake");

        Assert.Equal((4m, 14m), (Progress(s).Delta, Progress(s).Total));
        Assert.Equal(14m, s.Global(launched.GlobalEventId).Progress);
        Assert.Equal(2, s.Global(launched.GlobalEventId).Contributions.Count);
    }

    // ---- Runs ----

    [Fact]
    public void A_goal_of_runs_counts_each_completion_once_whatever_its_hours()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, RunsGoal(5));

        var first = s.Completed("Вася", "Silent Hill");
        Assert.Equal(new GlobalProgressChanged(launched.GlobalEventId, first, GlobalProgressReason.Completed, 1m, 1m), Progress(s));
        s.Completed("Петя", "Limbo");

        Assert.Equal((1m, 2m), (Progress(s).Delta, Progress(s).Total));
    }

    [Fact]
    public void An_hours_correction_does_not_move_a_goal_of_runs()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, RunsGoal(5));
        var run = s.Completed("Вася", "Silent Hill");

        s.NextRandom(1).Act(new CorrectRunHours(run, 13, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<GlobalProgressChanged>());
        Assert.Equal(1m, s.Global(launched.GlobalEventId).Progress);
    }

    // ---- Coop: one run (D-518, D-717) ----

    private static Scenario CoopSeason() => CoopTestScenario.New(r => r.WithGlobalEvents()).WithContent(Pack());

    [Fact]
    public void A_coop_run_counts_once_for_a_goal_of_runs()
    {
        var s = CoopSeason();
        var launched = s.Launch("banner", 72, RunsGoal(5));
        var run = s.CoopRun("Вася", "Петя");

        s.CompleteWith("Вася", 1, 1);

        Assert.Equal(new GlobalProgressChanged(launched.GlobalEventId, run, GlobalProgressReason.Completed, 1m, 1m), Progress(s, run));
    }

    // The progress events of the coop completion: read from the log, the completion command being behind CompleteWith
    private static GlobalProgressChanged Progress(Scenario s, Guid run) => Assert.Single(s.Log.OfType<GlobalProgressChanged>(), p => p.RunId == run);

    [Fact]
    public void A_coop_run_counts_its_hours_once_for_a_goal_of_hours()
    {
        var s = CoopSeason();
        var launched = s.Launch("banner", 72, HoursGoal(50));
        var run = s.CoopRun("Вася", "Петя");

        s.CompleteWith("Вася", 1, 1);

        Assert.Equal(6m, Progress(s, run).Delta);
        Assert.Equal(6m, s.Global(launched.GlobalEventId).Progress);
        Assert.Single(s.Global(launched.GlobalEventId).Contributions);
    }

    // ---- The window [start, end) ----

    [Fact]
    public void A_completion_before_the_launch_does_not_count()
    {
        var s = Season();
        s.Completed("Вася", "Silent Hill");

        var launched = s.Launch("banner", 72, HoursGoal(50));
        s.Completed("Петя", "Alan Wake");

        Assert.Equal(4m, s.Global(launched.GlobalEventId).Progress);
    }

    [Fact]
    public void A_run_rolled_before_the_launch_and_completed_in_its_window_counts()
    {
        // The window is by the moment of the completion (D-518)
        var s = Season();
        s.Playing("Вася", "Silent Hill");
        var launched = s.Launch("banner", 72, HoursGoal(50));

        s.CompleteOnes("Вася", 10);

        Assert.Equal(10m, s.Global(launched.GlobalEventId).Progress);
    }

    [Fact]
    public void A_completion_at_the_end_of_the_term_does_not_count_even_before_the_timer()
    {
        var s = Season();
        var launched = s.Launch("banner", 24, HoursGoal(50));
        s.Playing("Вася", "Silent Hill");
        s.Advance(launched.EndsAt - s.Clock.UtcNow);

        s.CompleteOnes("Вася", 10);

        Assert.Empty(s.LastEvents<GlobalProgressChanged>());
        Assert.Equal(0m, s.Global(launched.GlobalEventId).Progress);
    }

    [Fact]
    public void A_completion_a_minute_before_the_end_counts()
    {
        var s = Season();
        var launched = s.Launch("banner", 24, HoursGoal(50));
        s.Playing("Вася", "Silent Hill");
        s.Advance(launched.EndsAt - s.Clock.UtcNow - TimeSpan.FromMinutes(1));

        s.CompleteOnes("Вася", 10);

        Assert.Equal(10m, Progress(s).Delta);
    }

    [Fact]
    public void A_completion_after_the_admin_ended_the_event_does_not_count()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));
        s.Playing("Вася", "Silent Hill");
        s.Act(new EndGlobalEvent(launched.GlobalEventId));

        s.CompleteOnes("Вася", 10);

        Assert.Empty(s.LastEvents<GlobalProgressChanged>());
    }

    [Fact]
    public void A_completion_after_the_deadline_counts_nothing()
    {
        // Н-23, D-755: the roll was before the deadline, the completion after it, the scheduler has not closed the season
        // yet. The engine refuses the completion itself after the deadline (no throw of dice), so nothing can count.
        var s = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow.AddHours(3)));
        ScenarioAssert.Accepted(s);
        var launched = s.Launch("banner", 72, HoursGoal(50));
        s.Playing("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromHours(2));

        s.ExpectRejection().NextRandom(1, 1, 1).Complete("Вася");

        Assert.Equal(RejectionCodes.SeasonDeadlinePassed, s.Last.Rejection!.Code);
        Assert.Equal(0m, s.Global(launched.GlobalEventId).Progress);
        Assert.Empty(s.Log.OfType<GlobalProgressChanged>());
    }

    [Fact]
    public void A_completion_in_the_closing_season_counts_nothing()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));
        s.Playing("Вася", "Silent Hill");
        s.MoveStatusTo(SeasonStatus.Closing);

        s.ExpectRejection().NextRandom(1, 1, 1).Complete("Вася");

        Assert.False(s.Last.IsAccepted);
        Assert.Equal(0m, s.Global(launched.GlobalEventId).Progress);
        Assert.Empty(s.Log.OfType<GlobalProgressChanged>());
    }

    [Fact]
    public void One_completion_counts_for_every_running_event()
    {
        var s = Season();
        var hours = s.Launch("banner", 72, HoursGoal(50));
        var runs = s.Launch("karaoke", 72, RunsGoal(5));

        s.Completed("Вася", "Silent Hill");

        Assert.Equal(10m, s.Global(hours.GlobalEventId).Progress);
        Assert.Equal(1m, s.Global(runs.GlobalEventId).Progress);
        Assert.Equal(2, s.LastEvents<GlobalProgressChanged>().Count());
    }

    [Fact]
    public void An_event_without_a_goal_counts_no_progress()
    {
        var s = Season();
        var launched = s.Launch("banner", 72);

        s.Completed("Вася", "Silent Hill");

        Assert.Empty(s.LastEvents<GlobalProgressChanged>());
        Assert.Empty(s.Global(launched.GlobalEventId).Contributions);
    }

    // ---- Reached once, the reward in the same command ----

    [Fact]
    public void The_goal_is_reached_once_and_its_reward_is_queued_in_the_same_command()
    {
        var s = Season();
        var reward = OnAll(Coins(5));
        var launched = s.Launch("banner", 72, HoursGoal(12, reward));
        s.Completed("Вася", "Silent Hill");
        Assert.Empty(s.LastEvents<GlobalGoalReached>());

        var run = s.Completed("Петя", "Alan Wake");

        Assert.Equal((4m, 14m), (Progress(s).Delta, Progress(s).Total));
        Assert.Equal(new GlobalGoalReached(launched.GlobalEventId, run), Assert.Single(s.LastEvents<GlobalGoalReached>()));
        var queued = s.Queued();
        Assert.Equal((ForAllSource.GoalReward, launched.GlobalEventId, reward), (queued.Source, queued.Reference, queued.Effect));
        Assert.Equal(s.State.Players.Keys, queued.Players);
        Assert.True(s.Global(launched.GlobalEventId).Reached);

        var before = CoinsOf(s);
        s.SettleAll();
        Assert.All(before, p => Assert.Equal(p.Value + 5, s.State.Players[p.Key].Coins));

        // Past the goal: only progress
        s.Completed("Маша", "Dead Space");
        Assert.Equal(20m, Progress(s).Total);
        Assert.Empty(s.LastEvents<GlobalGoalReached>());
        Assert.Empty(s.LastEvents<ForAllQueued>());
        Assert.Single(s.Log.OfType<GlobalGoalReached>());
    }

    [Fact]
    public void Reaching_the_target_exactly_reaches_the_goal()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(10));

        var run = s.Completed("Вася", "Silent Hill");

        Assert.Equal(new GlobalGoalReached(launched.GlobalEventId, run), Assert.Single(s.LastEvents<GlobalGoalReached>()));
    }

    [Fact]
    public void One_short_of_the_target_does_not_reach_it()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(11));

        s.Completed("Вася", "Silent Hill");

        Assert.Empty(s.LastEvents<GlobalGoalReached>());
        Assert.False(s.Global(launched.GlobalEventId).Reached);
    }

    [Fact]
    public void A_goal_without_a_reward_is_reached_with_nothing_to_settle()
    {
        var s = Season();
        s.Launch("banner", 72, RunsGoal(1));

        s.Completed("Вася", "Silent Hill");

        Assert.Single(s.LastEvents<GlobalGoalReached>());
        Assert.Empty(s.LastEvents<ForAllQueued>());
    }

    // ---- A reject takes the contribution back ----

    [Fact]
    public void A_reject_takes_the_contribution_back_and_the_reached_reward_stays_and_the_goal_is_not_reached_again()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(12, OnAll(Coins(5))));
        s.Completed("Вася", "Silent Hill");
        var petya = s.Completed("Петя", "Alan Wake");
        s.SettleAll();
        var rewarded = s.Player("Маша").Coins;

        s.Act(new RejectProof(petya, "Фейк"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new GlobalProgressChanged(launched.GlobalEventId, petya, GlobalProgressReason.Rejected, -4m, 10m), Progress(s));
        Assert.True(s.Global(launched.GlobalEventId).Reached);
        Assert.DoesNotContain(s.Global(launched.GlobalEventId).Contributions, c => c.RunId == petya);
        Assert.Equal(rewarded, s.Player("Маша").Coins);

        s.Completed("Коля", "Dead Space");
        Assert.Equal(16m, Progress(s).Total);
        Assert.Empty(s.LastEvents<GlobalGoalReached>());
        Assert.Empty(s.LastEvents<ForAllQueued>());
    }

    [Fact]
    public void A_reject_before_the_goal_only_takes_the_contribution_back()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, RunsGoal(5));
        var run = s.Completed("Вася", "Silent Hill");

        s.Act(new RejectProof(run, "Фейк"));

        Assert.Equal(new GlobalProgressChanged(launched.GlobalEventId, run, GlobalProgressReason.Rejected, -1m, 0m), Progress(s));
        Assert.Empty(s.Global(launched.GlobalEventId).Contributions);
    }

    [Fact]
    public void A_reject_after_the_event_ended_still_takes_the_contribution_back()
    {
        // D-755: the progress shows the truth
        var s = Season();
        var launched = s.Launch("banner", 24, HoursGoal(50));
        var run = s.Completed("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromHours(24)).Act(new FireTimers());

        s.Act(new RejectProof(run, "Фейк"));

        Assert.Equal(-10m, Progress(s).Delta);
        Assert.Equal(0m, s.Global(launched.GlobalEventId).Progress);
    }

    [Fact]
    public void A_reject_of_a_run_that_brought_nothing_changes_no_progress()
    {
        var s = Season();
        var run = s.Completed("Вася", "Silent Hill");
        s.Launch("banner", 72, HoursGoal(50));

        s.Act(new RejectProof(run, "Фейк"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<GlobalProgressChanged>());
    }

    [Fact]
    public void An_approval_changes_no_progress()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));
        var run = s.Completed("Вася", "Silent Hill");

        s.Act(new ApproveProof(run, null, Comment));

        Assert.Empty(s.LastEvents<GlobalProgressChanged>());
        Assert.Equal(10m, s.Global(launched.GlobalEventId).Progress);
    }

    // ---- An hours correction moves the contribution by the difference (D-546 (6), Н-17) ----

    [Fact]
    public void An_hours_correction_up_adds_the_difference()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));
        var run = s.Completed("Вася", "Silent Hill");

        // 10 → 12 hours: 3 → 4 dice, one more rolled
        s.NextRandom(1).Act(new CorrectRunHours(run, 12, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new GlobalProgressChanged(launched.GlobalEventId, run, GlobalProgressReason.HoursCorrected, 2m, 12m), Progress(s));
        Assert.Equal([new GlobalContribution(run, 12m)], s.Global(launched.GlobalEventId).Contributions);
    }

    [Fact]
    public void An_hours_correction_down_takes_the_difference_and_a_reached_reward_stays()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(10, OnAll(Coins(5))));
        var run = s.Completed("Вася", "Silent Hill");
        s.SettleAll();
        var rewarded = s.Player("Петя").Coins;

        s.Act(new CorrectRunHours(run, 4, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new GlobalProgressChanged(launched.GlobalEventId, run, GlobalProgressReason.HoursCorrected, -6m, 4m), Progress(s));
        Assert.True(s.Global(launched.GlobalEventId).Reached);
        Assert.Equal(rewarded, s.Player("Петя").Coins);

        // Back up past the target: not reached a second time
        s.NextRandom(1, 1).Act(new CorrectRunHours(run, 10, Comment));
        Assert.Equal(10m, Progress(s).Total);
        Assert.Empty(s.LastEvents<GlobalGoalReached>());
    }

    [Fact]
    public void An_hours_correction_that_reaches_the_goal_while_the_event_runs_queues_the_reward_in_its_command()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(12, OnAll(Coins(5))));
        var run = s.Completed("Вася", "Silent Hill");

        s.NextRandom(1).Act(new CorrectRunHours(run, 12, Comment));

        Assert.Equal(new GlobalGoalReached(launched.GlobalEventId, run), Assert.Single(s.LastEvents<GlobalGoalReached>()));
        Assert.Equal(ForAllSource.GoalReward, s.Queued().Source);
    }

    [Fact]
    public void An_hours_correction_after_the_event_ended_moves_the_progress_but_reaches_nothing()
    {
        var s = Season();
        var launched = s.Launch("banner", 24, HoursGoal(12, OnAll(Coins(5))));
        var run = s.Completed("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromHours(24)).Act(new FireTimers());

        s.NextRandom(1).Act(new CorrectRunHours(run, 12, Comment));

        Assert.Equal(12m, Progress(s).Total);
        Assert.Empty(s.LastEvents<GlobalGoalReached>());
        Assert.Empty(s.LastEvents<ForAllQueued>());
        Assert.False(s.Global(launched.GlobalEventId).Reached);
    }

    [Fact]
    public void An_hours_correction_of_a_run_that_brought_nothing_changes_no_progress()
    {
        var s = Season();
        var run = s.Completed("Вася", "Silent Hill");
        s.Launch("banner", 72, HoursGoal(50));

        s.NextRandom(1).Act(new CorrectRunHours(run, 12, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<GlobalProgressChanged>());
    }

    // ---- The first finisher and the later ones (EV20) ----

    [Fact]
    public void The_first_finisher_contributes_and_gets_nothing()
    {
        // SPEC «Финишировавший первым может вносить вклад в кооп-цели»; D-518: he gets no reward (frozen, invariant 8)
        var s = Season(mapLength: 4);
        s.FinishWith("Маша", "Dead Space");
        var launched = s.Launch("banner", 72, HoursGoal(10, OnAll(Coins(5))));

        // Free mode
        var run = s.Completed("Маша", "Silent Hill");

        Assert.Equal(new GlobalProgressChanged(launched.GlobalEventId, run, GlobalProgressReason.Completed, 10m, 10m), Progress(s));
        Assert.Equal(new GlobalGoalReached(launched.GlobalEventId, run), Assert.Single(s.LastEvents<GlobalGoalReached>()));
        var queued = s.Queued();
        Assert.DoesNotContain(s.PlayerId("Маша"), queued.Players);
        var masha = s.Player("Маша").Coins;
        var vasya = s.Player("Вася").Coins;

        s.SettleAll();

        Assert.Equal(masha, s.Player("Маша").Coins);
        Assert.Equal(vasya + 5, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_later_finisher_contributes_and_gets_the_points_and_coins_of_the_reward()
    {
        var s = Season(mapLength: 4);
        s.FinishWith("Маша", "Dead Space");
        s.FinishWith("Коля", "Outlast");
        var launched = s.Launch("banner", 72, HoursGoal(10, OnAll(Coins(5), Points(1))));

        s.Completed("Коля", "Silent Hill");

        Assert.Equal(10m, s.Global(launched.GlobalEventId).Progress);
        Assert.Contains(s.PlayerId("Коля"), s.Queued().Players);
        var kolya = s.Player("Коля");
        s.SettleAll();
        Assert.Equal((kolya.Coins + 5, kolya.Points + 1), (s.Player("Коля").Coins, s.Player("Коля").Points));
    }

    [Fact]
    public void A_later_finisher_gets_nothing_of_a_reward_beyond_points_and_coins()
    {
        // D-700: a later finisher is a target only of an effect on points and coins alone (D-424: no object reaches him)
        var s = Season(mapLength: 4);
        s.FinishWith("Маша", "Dead Space");
        s.FinishWith("Коля", "Outlast");
        s.Launch("banner", 72, HoursGoal(10, OnAll(Coins(5), Give("orange"))));

        s.Completed("Петя", "Silent Hill");

        Assert.DoesNotContain(s.PlayerId("Коля"), s.Queued().Players);
        var kolya = s.Player("Коля").Coins;
        s.SettleAll();
        Assert.Equal((kolya, 0), (s.Player("Коля").Coins, s.Oranges("Коля")));
        Assert.Equal(1, s.Oranges("Вася"));
    }

    // ---- A player added after the launch (Н-16) ----

    [Fact]
    public void A_player_added_after_the_launch_contributes_and_gets_the_reward()
    {
        var s = Season();
        var launched = s.Launch("coin-rain", 72, HoursGoal(10, OnAll(Coins(5))));
        s.SettleAll();
        s.WithPlayers("Оля");

        var run = s.Completed("Оля", "Silent Hill");

        Assert.Equal(new GlobalGoalReached(launched.GlobalEventId, run), Assert.Single(s.LastEvents<GlobalGoalReached>()));
        Assert.Contains(s.PlayerId("Оля"), s.Queued().Players);
        var olya = s.Player("Оля").Coins;
        s.SettleAll();
        Assert.Equal(olya + 5, s.Player("Оля").Coins);
    }

    // ---- Undo of a contribution (D-750) ----

    [Fact]
    public void Undoing_a_completion_takes_its_contribution_back_even_after_the_end()
    {
        var s = Season();
        var launched = s.Launch("banner", 24, HoursGoal(50));
        s.Completed("Вася", "Silent Hill");
        var completion = s.LastCommandId;
        s.Advance(TimeSpan.FromHours(24)).Act(new FireTimers());

        s.Act(new UndoCommand(completion, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.Global(launched.GlobalEventId).Contributions);
        Assert.Equal(GlobalEventStatus.Ended, s.Global(launched.GlobalEventId).Status);
    }

    [Fact]
    public void Undoing_a_completion_before_the_one_that_reached_the_goal_is_refused()
    {
        // D-750: the reach holds every contribution before it
        var s = Season();
        s.Launch("banner", 72, HoursGoal(12));
        s.Completed("Вася", "Silent Hill");
        var first = s.LastCommandId;
        s.Completed("Петя", "Alan Wake");
        var reaching = s.LastCommandId;

        s.Act(new UndoCommand(first, "Ошибка админа"));

        Assert.False(s.Last.IsAccepted);
        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Contains(reaching, s.Last.Rejection.Related);
    }

    [Fact]
    public void Undoing_the_completion_that_reached_the_goal_waits_for_the_reward_batches_and_then_unreaches_it()
    {
        // D-701: the batches of the reward hold its cause; undone in reverse, the goal is no longer reached
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(10, OnAll(Coins(5))));
        var run = s.Completed("Вася", "Silent Hill");
        var reaching = s.LastCommandId;
        s.SettleAll();
        var batch = s.LastCommandId;
        var petya = s.Player("Петя").Coins;

        s.Act(new UndoCommand(reaching, "Ошибка админа"));
        Assert.False(s.Last.IsAccepted);
        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Contains(batch, s.Last.Rejection.Related);

        s.Act(new UndoCommand(batch, "Ошибка админа"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(petya - 5, s.Player("Петя").Coins);
        s.Act(new UndoCommand(reaching, "Ошибка админа"));
        ScenarioAssert.Accepted(s);

        var state = s.Global(launched.GlobalEventId);
        Assert.False(state.Reached);
        Assert.DoesNotContain(state.Contributions, c => c.RunId == run);
        Assert.Equal(0m, state.Progress);
    }

    [Fact]
    public void Undoing_a_reject_brings_the_contribution_back()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));
        var run = s.Completed("Вася", "Silent Hill");
        s.Act(new RejectProof(run, "Фейк"));

        s.Act(new UndoCommand(s.LastCommandId, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new GlobalContribution(run, 10m)], s.Global(launched.GlobalEventId).Contributions);
    }

    [Fact]
    public void Undoing_an_hours_correction_brings_the_contribution_back_to_the_old_hours()
    {
        var s = Season();
        var launched = s.Launch("banner", 72, HoursGoal(50));
        var run = s.Completed("Вася", "Silent Hill");
        s.NextRandom(1).Act(new CorrectRunHours(run, 12, Comment));

        s.Act(new UndoCommand(s.LastCommandId, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new GlobalContribution(run, 10m)], s.Global(launched.GlobalEventId).Contributions);
    }

    [Fact]
    public void The_log_with_progress_rejects_and_corrections_replays_to_the_same_state()
    {
        // EV49: progress is rebuilt from its events, the reward is not queued again
        var s = Season();
        s.Launch("banner", 72, HoursGoal(12, OnAll(Coins(5))));
        var run = s.Completed("Вася", "Silent Hill");
        s.NextRandom(1).Act(new CorrectRunHours(run, 12, Comment));
        s.SettleAll();
        var other = s.Completed("Петя", "Alan Wake");
        s.Act(new RejectProof(other, "Фейк"));

        var replay = SeasonEngine.Replay(s.StoredLog);

        Assert.Equal(s.State, replay);
        Assert.Single(s.Log.OfType<ForAllQueued>());
    }
}
