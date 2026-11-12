using GameEvent.Engine.Challenges;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Challenges;

/// <summary>
/// EV37, EV48 for the weekly challenge (D-516, S5.13-09): runs completed before the deadline count, the admin still marks
/// and rejects after it, a new challenge is not set after it, and the results wait for the proofs, so a reject still takes
/// the reward back before them.
/// </summary>
public class WeeklyChallengeDeadlineTests
{
    private static (Scenario S, Guid OldSchool, Guid Manual) Season()
    {
        var s = WeeklyChallengeScenario.New();
        var oldSchool = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var manual = s.SetChallenge(WeeklyChallengeScenario.Manual());
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(2)));
        ScenarioAssert.Accepted(s);
        return (s, oldSchool, manual);
    }

    private static void Close(Scenario s)
    {
        s.Advance(s.State.Deadline!.Value - s.Clock.UtcNow);
        s.Act(new ReachDeadline());
        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonStatus.Closing, s.State.Status);
    }

    [Fact]
    public void A_run_completed_just_before_the_deadline_completes_the_challenge_and_keeps_it_after_closing()
    {
        var (s, oldSchool, _) = Season();
        s.Play("Вася", "Silent Hill 3");
        s.Advance(s.State.Deadline!.Value - s.Clock.UtcNow - TimeSpan.FromSeconds(1));

        s.NextRandom(1, 1).Complete("Вася");
        Assert.Single(s.LastEvents<WeeklyChallengeCompleted>());

        Close(s);
        Assert.Empty(s.Last.Events.OfType<WeeklyChallengeRevoked>());
        Assert.Equal([s.PlayerId("Вася")], s.Completers(oldSchool));
    }

    [Fact]
    public void The_admin_marks_a_run_completed_before_the_deadline_after_it()
    {
        var (s, _, manual) = Season();
        var run = s.CompleteTitle("Вася", "Alan Wake");
        Close(s);

        s.Mark(manual, "Вася", run);

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<WeeklyChallengeCompleted>());
    }

    [Fact]
    public void A_reject_in_closing_takes_the_reward_back_before_the_results()
    {
        var (s, oldSchool, _) = Season();
        var run = s.CompleteTitle("Вася", "Silent Hill 3");
        Close(s);

        // The results wait for the proof of the run that gave the reward (EV48)
        s.Act(new ChangeSeasonStatus(SeasonStatus.Finished));
        Assert.Equal(RejectionCodes.SeasonProofsPending, s.Last.Rejection!.Code);

        s.Act(new RejectProof(run, "Фейк"));
        Assert.Single(s.LastEvents<WeeklyChallengeRevoked>());
        Assert.Empty(s.Completers(oldSchool));

        s.Act(new ChangeSeasonStatus(SeasonStatus.Finished));
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_new_challenge_is_not_set_after_the_deadline()
    {
        var (s, _, _) = Season();
        Close(s);

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new SetWeeklyChallenge(WeeklyChallengeScenario.HorrorWeek(), s.Clock.UtcNow));

        Assert.Contains(s.Last.Rejection!.Code, new[] { RejectionCodes.SeasonDeadlinePassed, RejectionCodes.SeasonNotActive });
        ScenarioAssert.Rejected(s, before, log, s.Last.Rejection.Code);
    }

    [Fact]
    public void A_new_challenge_is_not_set_at_the_deadline_of_a_season_still_active()
    {
        var (s, _, _) = Season();
        s.Advance(s.State.Deadline!.Value - s.Clock.UtcNow);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SetWeeklyChallenge(WeeklyChallengeScenario.HorrorWeek(), x.Clock.UtcNow)), RejectionCodes.SeasonDeadlinePassed);
    }
}
