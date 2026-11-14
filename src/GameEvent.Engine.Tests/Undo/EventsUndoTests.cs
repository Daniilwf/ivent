using GameEvent.Engine.Content;
using GameEvent.Engine.Duels;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Challenges;
using GameEvent.Engine.Tests.Coop;
using GameEvent.Engine.Tests.Events;
using GameEvent.Engine.Tests.GlobalEvents;
using GameEvent.Engine.Tests.Polls;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;
using DuelScenario = GameEvent.Engine.Tests.Duels.DuelScenario;

namespace GameEvent.Engine.Tests.Undo;

/// <summary>
/// EV38, task 5.15: the undo of the commands of stage 5 as a whole (SPEC «Откат — команда целиком», D-104), the cases the
/// tests of each mechanic leave out. A command nothing depends on is undone to exactly the season before it (13); one a
/// later command depends on is refused with that command named. Coop for two — the leave, the withdrawn, declined and
/// waiting invitation, the drop and the reject of a coop run, a coop completion with the weekly challenge and a global goal
/// it counted for both; duels — the declined and the withdrawn challenge; polls — a changed vote; run conditions — the
/// admin's «неприменимо». Written by <c>test-author</c> from the SPEC.
/// </summary>
public class EventsUndoTests
{
    private const string Comment = "Ошибка админа";

    private static Scenario Undone(Scenario s, Guid command)
    {
        s.Act(new UndoCommand(command, Comment));
        ScenarioAssert.Accepted(s);
        Assert.IsType<CommandUndone>(Assert.Single(s.Last.Events));
        return s;
    }

    // ---- Coop for two ----

    [Fact]
    public void Undoing_a_leave_brings_the_partner_back_into_the_run()
    {
        var s = New();
        var run = s.CoopRun();
        var before = s.State;
        s.Act(new LeaveCoop(s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        Assert.NotEqual(run, s.Player("Петя").ActiveRunId);

        Undone(s, s.LastCommandId);

        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal(run, s.Player("Петя").ActiveRunId);
        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);
    }

    [Fact]
    public void Undoing_a_leave_waits_for_the_partners_next_roll()
    {
        var s = New();
        s.CoopRun();
        s.Act(new LeaveCoop(s.PlayerId("Петя")));
        var leave = s.LastCommandId;
        s.RollTitle("Петя", SoloGame);
        var roll = s.LastCommandId;

        s.Rejects(new UndoCommand(leave, Comment), RejectionCodes.UndoDependents);

        Assert.Contains(roll, s.Last.Rejection!.Related);
    }

    [Fact]
    public void Undoing_a_withdrawn_invitation_lets_it_wait_and_be_accepted_again()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var invitation = s.Invite("Вася", "Петя");
        var before = s.State;
        s.Act(new WithdrawInvitation(s.PlayerId("Вася"), invitation));
        ScenarioAssert.Accepted(s);

        Undone(s, s.LastCommandId);

        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal(InteractionStatus.Pending, CoopTestScenario.Invitation(s, invitation).Status);
        s.Accept("Петя", invitation);
        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);
    }

    [Fact]
    public void Undoing_a_declined_invitation_lets_it_be_answered_again()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var invitation = s.Invite("Вася", "Петя");
        var before = s.State;
        CoopTestScenario.Answer(s, "Петя", invitation, accept: false);
        ScenarioAssert.Accepted(s);

        Undone(s, s.LastCommandId);

        Assert.Equal(Comparable(before), Comparable(s.State));
        s.Accept("Петя", invitation);
        Assert.Equal(s.PlayerId("Петя"), s.State.Runs[run].PartnerId);
    }

    [Fact]
    public void Undoing_a_waiting_invitation_removes_it_and_frees_its_place_in_the_limit()
    {
        // At most one pending invitation sent: while it waits, another is refused
        var s = New(r => r.WithCoop(maxOpenInvites: 1));
        s.PlayTitle("Вася", CoopGame);
        var before = s.State;
        var invitation = s.Invite("Вася", "Петя");
        var invited = s.LastCommandId;
        s.Act(new InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Маша")));
        Assert.False(s.Last.IsAccepted, "A second pending invitation was taken.");

        Undone(s, invited);

        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.False(s.State.Interactions.ContainsKey(invitation));
        Assert.DoesNotContain(s.State.Interactions.Values, i => i.FromPlayerId == s.PlayerId("Вася"));

        // Its place in the limit is free again: the next invitation is taken
        var next = s.Invite("Вася", "Маша");
        Assert.Equal(s.PlayerId("Маша"), s.State.Interactions[next].ToPlayerId);
    }

    [Fact]
    public void Undoing_an_invitation_after_its_answer_waits_for_the_answer()
    {
        var s = New();
        s.PlayTitle("Вася", CoopGame);
        var invitation = s.Invite("Вася", "Петя");
        var invite = s.LastCommandId;
        CoopTestScenario.Answer(s, "Петя", invitation, accept: false);
        var answer = s.LastCommandId;

        s.Rejects(new UndoCommand(invite, Comment), RejectionCodes.UndoDependents);
        Assert.Contains(answer, s.Last.Rejection!.Related);

        Undone(s, answer);
        Undone(s, invite);
        Assert.False(s.State.Interactions.ContainsKey(invitation));
    }

    [Fact]
    public void Undoing_the_drop_of_a_coop_run_brings_both_back_into_it()
    {
        var s = New();
        var run = s.CoopRun();
        var before = s.State;
        s.Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[run].Status);

        Undone(s, s.LastCommandId);

        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal((run, run), (s.Player("Вася").ActiveRunId!.Value, s.Player("Петя").ActiveRunId!.Value));
    }

    [Fact]
    public void Undoing_the_reject_of_a_coop_run_gives_both_their_shares_back()
    {
        // 3 + 1 = 4 points split 2 / 2, 6 coins split 3 / 3 (D-521)
        var s = New();
        var run = s.CoopRun();
        s.CompleteWith("Вася", 3, 1);
        var before = s.State;
        Assert.Equal((2, 2), (s.Player("Вася").Points, s.Player("Петя").Points));
        s.Act(new RejectProof(run, "На скрине другая игра"));
        ScenarioAssert.Accepted(s);
        Assert.Equal((0, 0), (s.Player("Вася").Points, s.Player("Петя").Points));

        Undone(s, s.LastCommandId);

        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal((2, 2), (s.Player("Вася").Points, s.Player("Петя").Points));
        Assert.Equal(RunStatus.Completed, s.State.Runs[run].Status);
    }

    [Fact]
    public void Undoing_a_coop_completion_takes_back_both_shares_both_challenge_rewards_and_its_contribution_to_a_goal()
    {
        // Given the weekly challenge «пройди игру» and a global goal of 100 hours: the coop completion counts for both
        var s = New(r => r.WithWeeklyChallenge().WithGlobalEvents());
        s.WithContent(GlobalSetup.Pack());
        var challenge = s.SetChallenge(new WeeklyChallengeDefinition
        {
            Id = "any-game",
            Name = "Любая игра",
            Description = "Пройди любую игру.",
            Condition = new ConditionSpec { Game = new GameFilterSpec { Tags = ["Games"] } },
            Reward = new RewardSpec { Coins = 5 },
            Check = ChallengeCheck.Auto,
        });
        var global = s.Launch("plain-coins", 240, GlobalSetup.HoursGoal(100)).GlobalEventId;
        GlobalSetup.SettleAll(s);
        var run = s.CoopRun();
        var before = s.State;
        s.CompleteWith("Вася", 3, 1);
        var completion = s.LastCommandId;
        Assert.Equal(["Вася", "Петя"], s.Completers(challenge).Select(s.PlayerName).Order(StringComparer.Ordinal));
        Assert.Equal(GameHours, s.State.GlobalEvents[global].Progress);

        // When the completion is undone
        Undone(s, completion);

        // Then both lose their shares and their rewards, the goal its hours: the season before the completion
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Empty(s.Completers(challenge));
        Assert.Empty(s.State.GlobalEvents[global].Contributions);
        Assert.Equal(RunStatus.Playing, s.State.Runs[run].Status);
    }

    // ---- Duels ----

    [Fact]
    public void Undoing_a_declined_challenge_lets_it_wait_and_be_accepted_again()
    {
        var s = DuelScenario.New();
        var duel = DuelScenario.Challenged(s, "Вася", "Петя");
        var before = s.State;
        DuelScenario.Answer(s, "Петя", duel, accept: false);
        ScenarioAssert.Accepted(s);

        Undone(s, s.LastCommandId);

        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal(InteractionStatus.Pending, DuelScenario.Invitation(s, duel).Status);
        DuelScenario.Accepted(s, "Петя", duel);
        Assert.Equal(DuelStatus.Active, DuelScenario.Duel(s, duel).Status);
    }

    [Fact]
    public void Undoing_a_withdrawn_challenge_lets_it_wait_again()
    {
        var s = DuelScenario.New();
        var duel = DuelScenario.Challenged(s, "Вася", "Петя");
        var before = s.State;
        DuelScenario.Withdraw(s, "Вася", duel);
        ScenarioAssert.Accepted(s);

        Undone(s, s.LastCommandId);

        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal(InteractionStatus.Pending, DuelScenario.Invitation(s, duel).Status);
    }

    // ---- Polls ----

    [Fact]
    public void Undoing_a_changed_vote_brings_the_first_vote_back()
    {
        var s = PollSetup.Season();
        var poll = s.Start(PollSetup.ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");
        var before = s.State;
        s.Voted(poll.PollId, "Вася", "RPG");
        Assert.IsType<VoteChanged>(Assert.Single(s.Last.Events));

        Undone(s, s.LastCommandId);

        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Equal([new Vote(s.Player("Вася").UserId, "Horror")], s.State.Polls[poll.PollId].Votes);
    }

    [Fact]
    public void Undoing_the_first_vote_waits_for_its_change()
    {
        var s = PollSetup.Season();
        var poll = s.Start(PollSetup.ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");
        var first = s.LastCommandId;
        s.Voted(poll.PollId, "Вася", "RPG");
        var change = s.LastCommandId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(first, Comment)), RejectionCodes.UndoDependents);

        Assert.Contains(change, s.Last.Rejection!.Related);
    }

    // ---- Run conditions ----

    [Fact]
    public void Undoing_a_waiver_takes_the_dice_back_and_the_condition_off_the_next_roll()
    {
        // Given «не ниже сложной» unmet on normal: the run went without dice
        var s = RunConditionTests.Season();
        s.Give("Вася", "vow-hard");
        s.Used("Вася", "vow-hard");
        s.RollTitle("Вася", "Alan Wake").Start("Вася");
        var run = s.Player("Вася").ActiveRunId!.Value;
        s.Complete("Вася", Difficulty.Normal);
        ScenarioAssert.Accepted(s);
        var before = s.State;

        // When the admin's «неприменимо» is undone
        s.NextRandom(3, 1).Act(new WaiveRunCondition(run, "В игре нет сложностей"));
        ScenarioAssert.Accepted(s);
        Undone(s, s.LastCommandId);

        // Then the run is without dice again and the condition does not wait for the next roll
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Empty(s.State.Runs[run].Dice);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
    }

    [Fact]
    public void Undoing_a_waiver_waits_for_the_roll_that_took_the_condition()
    {
        var s = RunConditionTests.Season();
        s.Give("Вася", "vow-hard");
        s.Used("Вася", "vow-hard");
        s.RollTitle("Вася", "Alan Wake").Start("Вася");
        var run = s.Player("Вася").ActiveRunId!.Value;
        s.Complete("Вася", Difficulty.Normal);
        s.NextRandom(3, 1).Act(new WaiveRunCondition(run, "В игре нет сложностей"));
        var waiver = s.LastCommandId;
        s.Roll("Вася");
        var roll = s.LastCommandId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UndoCommand(waiver, Comment)), RejectionCodes.UndoDependents);

        Assert.Contains(roll, s.Last.Rejection!.Related);
    }
}
