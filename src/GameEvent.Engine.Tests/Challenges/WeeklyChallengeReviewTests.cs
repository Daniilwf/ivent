using GameEvent.Engine.Challenges;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Challenges;

/// <summary>
/// Found by the review of task 5.13 (D-578): completions of different players are independent for an undo (EV38); the
/// flag waits for the proofs of the runs completions stand on (EV35, D-574); a reject leaves the frozen first finisher's
/// coins (D-99, D-422 (2)); a window that would end past the calendar is refused.
/// </summary>
public class WeeklyChallengeReviewTests
{
    private static void AssertAsTheLogWithout(Scenario s)
    {
        ScenarioAssert.Accepted(s);
        // Counters such as a player's points tick are never wound back (D-104): the challenges and the coins are compared
        var replayed = SeasonEngine.Replay(s.EffectiveLog);
        Assert.Equal(replayed.WeeklyChallenges, s.State.WeeklyChallenges);
        Assert.Equal(replayed.Players.Values.Select(p => p.Coins), s.State.Players.Values.Select(p => p.Coins));
    }

    // ---- Undo: one completion does not depend on another player's ----

    [Fact]
    public void Undoing_one_players_completion_does_not_wait_for_another_players_completion()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var vasyaCoins = s.Player("Вася").Coins;
        s.CompleteTitle("Вася", "Silent Hill 3");
        var vasya = s.LastCommandId;
        s.CompleteTitle("Петя", "Doom 3");
        var petyaCoins = s.Player("Петя").Coins;

        s.Act(new UndoCommand(vasya, "Завершение по ошибке"));

        AssertAsTheLogWithout(s);
        Assert.Equal([s.PlayerId("Петя")], s.Completers(challenge));
        Assert.Equal(vasyaCoins, s.Player("Вася").Coins);
        Assert.Equal(petyaCoins, s.Player("Петя").Coins);
    }

    [Fact]
    public void Undoing_the_reject_of_one_players_run_does_not_wait_for_another_players_completion()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");
        s.Act(new RejectProof(run, "Не та игра"));
        var rejected = s.LastCommandId;
        s.CompleteTitle("Петя", "Doom 3");

        s.Act(new UndoCommand(rejected, "Пруф был верный"));

        AssertAsTheLogWithout(s);
        Assert.Equal([s.PlayerId("Вася"), s.PlayerId("Петя")], s.Completers(challenge).Order());
    }

    [Fact]
    public void Undoing_one_players_mark_does_not_wait_for_another_players_mark()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.Manual());
        var vasyaRun = s.CompleteTitle("Вася", "Alan Wake");
        var petyaRun = s.CompleteTitle("Петя", "Outlast");
        s.Mark(challenge, "Вася", vasyaRun);
        var vasya = s.LastCommandId;
        s.Mark(challenge, "Петя", petyaRun);

        s.Act(new UndoCommand(vasya, "Отметил не того"));

        AssertAsTheLogWithout(s);
        Assert.Equal([s.PlayerId("Петя")], s.Completers(challenge));
    }

    [Fact]
    public void Undoing_a_players_completion_still_waits_for_his_own_later_reject()
    {
        var s = WeeklyChallengeScenario.New();
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");
        var completed = s.LastCommandId;
        s.Act(new RejectProof(run, "Не та игра"));
        var rejected = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(completed, "Завершение по ошибке"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(rejected, s.Last.Rejection!.Related);
    }

    [Fact]
    public void Undoing_a_manual_challenge_waits_for_its_marks()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.Manual());
        var set = s.LastCommandId;
        var run = s.CompleteTitle("Вася", "Alan Wake");
        s.Mark(challenge, "Вася", run);
        var marked = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(set, "Не та неделя"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(marked, s.Last.Rejection!.Related);
    }

    // ---- The flag waits for the proofs (EV35, D-574) ----

    private static ChangeRuleset Off(Scenario s) =>
        new(s.Ruleset with { Features = s.Ruleset.Features with { WeeklyChallenge = false } });

    [Fact]
    public void The_challenge_is_not_switched_off_while_a_completion_waits_for_its_proof()
    {
        var s = WeeklyChallengeScenario.New();
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.ExpectRejection().Act(Off(x)), RejectionCodes.WeeklyChallengeInPlay);
        Assert.True(s.State.Rules.Features.WeeklyChallenge);

        s.Act(new ApproveProof(run, null, "Видел стрим"));
        ScenarioAssert.Accepted(s);
        s.Act(Off(s));
        ScenarioAssert.Accepted(s);
        Assert.False(s.State.Rules.Features.WeeklyChallenge);
    }

    [Fact]
    public void A_rejected_completion_lets_the_challenge_be_switched_off()
    {
        var s = WeeklyChallengeScenario.New();
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");
        s.Act(new RejectProof(run, "Не та игра"));

        s.Act(Off(s));

        ScenarioAssert.Accepted(s);
        Assert.False(s.State.Rules.Features.WeeklyChallenge);
    }

    [Fact]
    public void A_completion_without_coins_holds_the_flag_too()
    {
        // The frozen first's completion pays nothing, but a reject still has to take the completion back
        var s = WeeklyChallengeScenario.New(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }, mapLength: 2);
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.CompleteTitle("Вася", "Alan Wake");
        s.CompleteTitle("Вася", "Silent Hill 3");
        Assert.Equal(0, Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()).Coins);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.ExpectRejection().Act(Off(x)), RejectionCodes.WeeklyChallengeInPlay);
    }

    [Fact]
    public void Other_changes_of_the_rules_go_through_while_a_completion_waits()
    {
        var s = WeeklyChallengeScenario.New();
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.CompleteTitle("Вася", "Silent Hill 3");

        s.Act(new ChangeRuleset(s.Ruleset with { Reward = s.Ruleset.Reward with { DiceCount = s.Ruleset.Reward.DiceCount with { Max = 12 } } }));

        ScenarioAssert.Accepted(s);
        Assert.True(s.State.Rules.Features.WeeklyChallenge);
    }

    // ---- The frozen first finisher's coins (D-99, D-422 (2)) ----

    [Fact]
    public void A_reject_after_the_freeze_takes_the_completion_but_leaves_the_frozen_first_his_coins()
    {
        var s = WeeklyChallengeScenario.New(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }, mapLength: 4);
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3"); // 2 steps, 10 coins of the challenge
        Assert.Equal(10, Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()).Coins);
        s.CompleteTitle("Вася", "Alan Wake"); // 2 more: the finish, frozen at once
        Assert.True(s.Player("Вася").Finish!.Frozen);
        var coins = s.Player("Вася").Coins;

        s.Act(new RejectProof(run, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new WeeklyChallengeRevoked(challenge, s.PlayerId("Вася"), run, 0), Assert.Single(s.LastEvents<WeeklyChallengeRevoked>()));
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.Reason == CoinsReason.WeeklyChallengeRevoked);
        Assert.Equal(coins, s.Player("Вася").Coins);
        Assert.Empty(s.Completers(challenge));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void A_reject_before_the_freeze_takes_the_coins_of_the_provisional_first()
    {
        // Not frozen yet (the finish waits for its proofs): the provisional first is counted as everyone (D-99)
        var s = WeeklyChallengeScenario.New(mapLength: 4);
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");
        s.CompleteTitle("Вася", "Alan Wake");
        Assert.False(s.Player("Вася").Finish!.Frozen);

        s.Act(new RejectProof(run, "На скрине другая игра"));

        Assert.Equal(new WeeklyChallengeRevoked(challenge, s.PlayerId("Вася"), run, 10), Assert.Single(s.LastEvents<WeeklyChallengeRevoked>()));
        Assert.Contains(new CoinsChanged(s.PlayerId("Вася"), -10, CoinsReason.WeeklyChallengeRevoked, run), s.Last.Events);
    }

    // ---- The window ----

    [Fact]
    public void A_window_that_would_end_past_the_calendar_is_refused()
    {
        var s = WeeklyChallengeScenario.New();

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Act(new SetWeeklyChallenge(WeeklyChallengeScenario.OldSchool(), DateTimeOffset.MaxValue.AddDays(-3))),
            RejectionCodes.WeeklyChallengeWindowInvalid);
    }

    [Fact]
    public void A_window_whose_clock_time_would_end_past_the_calendar_is_refused()
    {
        // In UTC the end still fits, in the start's own offset it does not
        var s = WeeklyChallengeScenario.New();
        var startsAt = new DateTimeOffset(DateTime.MaxValue.AddDays(-3), TimeSpan.FromHours(3));

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SetWeeklyChallenge(WeeklyChallengeScenario.OldSchool(), startsAt)), RejectionCodes.WeeklyChallengeWindowInvalid);
    }

    [Fact]
    public void A_window_that_ends_on_the_last_day_of_the_calendar_is_set()
    {
        var s = WeeklyChallengeScenario.New();
        var startsAt = DateTimeOffset.MaxValue.AddDays(-WeeklyChallengeScenario.WindowDays);

        s.Act(new SetWeeklyChallenge(WeeklyChallengeScenario.OldSchool(), startsAt));

        ScenarioAssert.Accepted(s);
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(s.LastEvents<WeeklyChallengeSet>()).EndsAt);
    }
}
