using GameEvent.Engine.Duels;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Duels;

/// <summary>
/// EV38 for duels (D-104, task 5.12): undoing the winning completion puts the race back and takes its coins; undoing a
/// forfeiting drop does the same; undoing an accept or a challenge while nothing depends on it takes it back, and waits
/// for the commands that settled the duel otherwise.
/// </summary>
public class DuelUndoTests
{
    private static void AssertAsTheLogWithout(Scenario s)
    {
        ScenarioAssert.Accepted(s);
        var replayed = SeasonEngine.Replay(s.EffectiveLog);
        Assert.Equal(replayed.Duels, s.State.Duels);
        Assert.Equal(replayed.Players.Values.Select(p => p.Coins), s.State.Players.Values.Select(p => p.Coins));
    }

    [Fact]
    public void Undoing_the_winning_completion_puts_the_race_back_and_takes_the_coins()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var race = s.Duel(duel);
        s.Play("Вася", "Silent Hill");
        var coins = s.Player("Вася").Coins;
        s.CompleteActive("Вася");
        Assert.Equal(DuelStatus.Won, s.Duel(duel).Status);
        var completed = s.LastCommandId;

        s.Act(new UndoCommand(completed, "Завершение по ошибке"));

        AssertAsTheLogWithout(s);
        Assert.Equal(race, s.Duel(duel));
        Assert.Equal(coins, s.Player("Вася").Coins);
    }

    [Fact]
    public void Undoing_a_forfeiting_drop_puts_the_race_back_and_takes_the_winners_coins()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var race = s.Duel(duel);
        s.Play("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromHours(2));
        var petyaCoins = s.Player("Петя").Coins;
        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
        Assert.Equal(DuelEnd.Forfeit, s.Duel(duel).End);

        s.Act(new UndoCommand(s.LastCommandId, "Дроп по ошибке"));

        AssertAsTheLogWithout(s);
        Assert.Equal(race, s.Duel(duel));
        Assert.Equal(petyaCoins, s.Player("Петя").Coins);
    }

    [Fact]
    public void Undoing_a_reject_gives_the_win_and_its_coins_back()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill");
        var won = s.Duel(duel);
        var coins = s.Player("Вася").Coins;
        s.Act(new RejectProof(run, "Не та игра"));

        s.Act(new UndoCommand(s.LastCommandId, "Пруф был верный"));

        AssertAsTheLogWithout(s);
        Assert.Equal(won, s.Duel(duel));
        Assert.Equal(coins, s.Player("Вася").Coins);
    }

    [Fact]
    public void Undoing_an_accept_nothing_depends_on_lets_the_challenge_wait_again()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        var waiting = s.Duel(duel);
        s.Accepted("Петя", duel);

        s.Act(new UndoCommand(s.LastCommandId, "Нажал не то"));

        AssertAsTheLogWithout(s);
        Assert.Equal(waiting, s.Duel(duel));
    }

    [Fact]
    public void Undoing_the_accept_of_a_settled_duel_waits_for_the_completion_that_settled_it()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        s.Accepted("Петя", duel);
        var accepted = s.LastCommandId;
        s.CompleteTitle("Вася", "Silent Hill");
        var completed = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(accepted, "Нажал не то"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(completed, s.Last.Rejection!.Related);

        s.Act(new UndoCommand(completed, "Завершение по ошибке"));
        AssertAsTheLogWithout(s);
        s.Act(new UndoCommand(accepted, "Нажал не то"));
        AssertAsTheLogWithout(s);
        Assert.Equal(DuelStatus.Pending, s.Duel(duel).Status);
    }

    [Fact]
    public void Undoing_a_waiting_challenge_removes_it_and_frees_the_place()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");

        s.Act(new UndoCommand(s.LastCommandId, "Вызвал не того"));

        AssertAsTheLogWithout(s);
        Assert.False(s.State.Duels.ContainsKey(duel));
        s.Challenge("Вася", "Маша");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Undoing_a_challenge_after_its_answer_waits_for_the_answer()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        var challenged = s.LastCommandId;
        s.Answer("Петя", duel, accept: false);
        var answered = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(challenged, "Вызвал не того"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(answered, s.Last.Rejection!.Related);
    }
}
