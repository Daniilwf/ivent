using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Challenges;

/// <summary>
/// EV38 for the weekly challenge (S5.13-12, D-104): undoing a completion takes its challenge completion and coins with it;
/// undoing a mark takes the mark; undoing the setting of a challenge waits for the commands that completed it.
/// </summary>
public class WeeklyChallengeUndoTests
{
    private static void AssertUndoneToTheLogWithout(Scenario s)
    {
        Assert.True(s.Last.IsAccepted, s.Last.Rejection?.ToString());
        var replayed = SeasonEngine.Replay(s.EffectiveLog);
        Assert.Equal(replayed.Players, s.State.Players);
        Assert.Equal(replayed.WeeklyChallenges, s.State.WeeklyChallenges);
    }

    [Fact]
    public void Undoing_a_completion_takes_the_challenge_completion_and_its_coins_back()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var coins = s.Player("Вася").Coins;
        s.CompleteTitle("Вася", "Silent Hill 3");
        var completed = s.LastCommandId;

        s.Act(new UndoCommand(completed, "Завершение по ошибке"));

        AssertUndoneToTheLogWithout(s);
        Assert.Empty(s.Completers(challenge));
        Assert.Equal(coins, s.Player("Вася").Coins);
    }

    [Fact]
    public void Undoing_a_mark_takes_it_back()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.Manual());
        var run = s.CompleteTitle("Вася", "Alan Wake");
        var coins = s.Player("Вася").Coins;
        s.Mark(challenge, "Вася", run);

        s.Act(new UndoCommand(s.LastCommandId, "Отметил не того"));

        AssertUndoneToTheLogWithout(s);
        Assert.Empty(s.Completers(challenge));
        Assert.Equal(coins, s.Player("Вася").Coins);
    }

    [Fact]
    public void Undoing_a_reject_gives_the_completion_back()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");
        s.Act(new RejectProof(run, "Не та игра"));

        s.Act(new UndoCommand(s.LastCommandId, "Пруф был верный"));

        AssertUndoneToTheLogWithout(s);
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    [Fact]
    public void Undoing_a_challenge_nobody_completed_removes_it()
    {
        var s = WeeklyChallengeScenario.New();
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());

        s.Act(new UndoCommand(s.LastCommandId, "Не та неделя"));

        AssertUndoneToTheLogWithout(s);
        Assert.Empty(s.State.WeeklyChallenges);
    }

    [Fact]
    public void Undoing_a_challenge_after_completions_waits_for_them()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var set = s.LastCommandId;
        s.CompleteTitle("Вася", "Silent Hill 3");
        var completed = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(set, "Не та неделя"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(completed, s.Last.Rejection!.Related);

        s.Act(new UndoCommand(completed, "Завершение по ошибке"));
        AssertUndoneToTheLogWithout(s);
        s.Act(new UndoCommand(set, "Не та неделя"));
        AssertUndoneToTheLogWithout(s);
        Assert.False(s.State.WeeklyChallenges.ContainsKey(challenge));
    }
}
