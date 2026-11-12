using GameEvent.Engine.Challenges;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Challenges;

/// <summary>
/// S5.13-08 (EV33 with EV26): a coop run completes the challenge for both, each with the whole reward — the challenge is
/// settled per player and is not split like the run's own coins (D-521, the panel's verdict). The coop commands are the
/// glossary's names of task 5.10 (<c>InviteToCoop</c>, <c>AnswerInvitation</c>, <c>RunState.PartnerId</c>); when 5.10 lands
/// with other signatures, only the setup here changes, not what is checked.
/// </summary>
public class WeeklyChallengeCoopTests
{
    private static (Scenario S, Guid Challenge, Guid Run) CoopRun()
    {
        var s = WeeklyChallengeScenario.New(r => r with { Features = r.Features with { Coop = true } });
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.Play("Вася", "Silent Hill 3");
        s.Act(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        var invitation = Assert.Single(s.LastEvents<InteractionCreated>()).Interaction.InteractionId;
        s.Act(new AnswerInvitation(s.PlayerId("Петя"), invitation, Accept: true));
        ScenarioAssert.Accepted(s);
        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);
        return (s, challenge, run);
    }

    [Fact]
    public void A_coop_run_completes_the_challenge_for_both_with_the_whole_reward_each()
    {
        var (s, challenge, run) = CoopRun();

        s.NextRandom(1, 1).Complete("Вася");

        Assert.Equal(
            new[] { new WeeklyChallengeCompleted(challenge, s.PlayerId("Вася"), run, 10), new WeeklyChallengeCompleted(challenge, s.PlayerId("Петя"), run, 10) }
                .OrderBy(c => c.PlayerId),
            s.LastEvents<WeeklyChallengeCompleted>().OrderBy(c => c.PlayerId));
        foreach (var player in new[] { "Вася", "Петя" })
        {
            Assert.Contains(new CoinsChanged(s.PlayerId(player), 10, CoinsReason.WeeklyChallenge, run), s.Last.Events);
        }

        Assert.Equal(new[] { s.PlayerId("Вася"), s.PlayerId("Петя") }.Order(), s.Completers(challenge).Order());
    }

    [Fact]
    public void A_reject_of_the_coop_run_takes_the_completion_back_from_both()
    {
        var (s, challenge, run) = CoopRun();
        s.NextRandom(1, 1).Complete("Вася");

        s.Act(new RejectProof(run, "Фейк"));

        Assert.Equal(2, s.LastEvents<WeeklyChallengeRevoked>().Count());
        Assert.Empty(s.Completers(challenge));
    }

    [Fact]
    public void A_partner_who_already_completed_the_challenge_gets_nothing_more()
    {
        var s = WeeklyChallengeScenario.New(r => r with { Features = r.Features with { Coop = true } });
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.CompleteTitle("Петя", "Doom 3");
        var run = s.Play("Вася", "Silent Hill 3");
        s.Act(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")));
        var invitation = Assert.Single(s.LastEvents<InteractionCreated>()).Interaction.InteractionId;
        s.Act(new AnswerInvitation(s.PlayerId("Петя"), invitation, Accept: true));

        s.NextRandom(1, 1).Complete("Вася");

        Assert.Equal(new WeeklyChallengeCompleted(challenge, s.PlayerId("Вася"), run, 10), Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()));
    }
}
