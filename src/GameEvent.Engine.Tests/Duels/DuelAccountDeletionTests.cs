using GameEvent.Engine.Duels;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Duels;

/// <summary>
/// The system command of an account's deletion in a season (D-734, decided by the owner 2026-11-14; D-792): the player's
/// races are called off without a winner or coins (<see cref="DuelEnd.AccountDeleted"/>), his pending challenges burn both
/// ways (<see cref="InteractionExpiry.AccountDeleted"/>), others' duels stay; nothing to cancel is refused and writes
/// nothing; the deadline does not stop it (nor does the flag: duels are not switched off with anything open, D-730), an unknown player and a season not running do; it is undone as a
/// whole and holds the undo of what it closed.
/// </summary>
public class DuelAccountDeletionTests
{
    private static CancelDuelsOfDeletedAccount Deleted(Scenario s, string player) => new(s.PlayerId(player));

    private static void Rejects(Scenario s, ICommand command, string code) => ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(command), code);

    [Fact]
    public void A_race_of_the_deleted_player_is_called_off_without_a_winner_or_coins()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var coins = (s.Player("Вася").Coins, s.Player("Петя").Coins);

        s.Act(Deleted(s, "Петя"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new DuelSettled(duel, DuelStatus.Cancelled, DuelEnd.AccountDeleted, null, null, 0, s.Clock.UtcNow),
            Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Empty(s.DuelCoins());
        Assert.Equal(coins, (s.Player("Вася").Coins, s.Player("Петя").Coins));
        Assert.Equal((DuelStatus.Cancelled, (Guid?)null), (s.Duel(duel).Status, s.Duel(duel).WinnerId));
        Assert.False(s.Player("Петя").IsInactive);
        Assert.Equal(SeasonEngine.Replay(s.Log), s.State);
    }

    [Fact]
    public void The_pending_challenges_of_the_deleted_player_burn_both_ways_and_others_stay()
    {
        // Up to three open duels each, so Маша may have a challenge sent and one received
        var s = DuelScenario.New(duels: d => d with { MaxOpenPerPlayer = 3 });
        var sent = s.Challenged("Маша", "Коля");
        var received = s.Challenged("Петя", "Маша");
        var others = s.Challenged("Вася", "Коля");

        s.Act(Deleted(s, "Маша"));

        ScenarioAssert.Accepted(s);
        var now = s.Clock.UtcNow;
        Assert.Equal(
            new[] { new InteractionExpired(sent, InteractionExpiry.AccountDeleted, now), new InteractionExpired(received, InteractionExpiry.AccountDeleted, now) }
                .OrderBy(e => e.InteractionId),
            s.LastEvents<InteractionExpired>().OrderBy(e => e.InteractionId));
        Assert.Equal(InteractionStatus.Expired, s.Invitation(sent).Status);
        Assert.Equal(InteractionStatus.Expired, s.Invitation(received).Status);
        Assert.Equal(InteractionStatus.Pending, s.Invitation(others).Status);
        Assert.Empty(s.LastEvents<DuelSettled>());
    }

    [Fact]
    public void A_player_with_nothing_open_is_refused_and_the_duels_of_others_stay()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");

        Rejects(s, Deleted(s, "Маша"), RejectionCodes.DuelNothingToCancel);
    }

    [Fact]
    public void Called_off_once_a_repeat_has_nothing_to_cancel()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        s.Act(Deleted(s, "Петя"));
        ScenarioAssert.Accepted(s);

        Rejects(s, Deleted(s, "Петя"), RejectionCodes.DuelNothingToCancel);
    }

    [Fact]
    public void A_player_not_in_the_season_is_refused()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");

        Rejects(s, new CancelDuelsOfDeletedAccount(SequentialIds.Make(0x7D000000, 1)), RejectionCodes.PlayerUnknown);
    }

    [Fact]
    public void A_season_that_is_not_running_is_refused()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        ScenarioAssert.Accepted(s);
        Assert.Equal(DuelStatus.Drawn, s.Duel(duel).Status);

        Rejects(s, Deleted(s, "Петя"), RejectionCodes.SeasonNotActive);
    }

    [Fact]
    public void Past_the_deadline_of_a_season_still_running_what_is_left_is_still_called_off()
    {
        // The command closes what is left: the deadline does not stop it, unlike the players' own duel commands (D-516)
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(2)));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromDays(2));

        s.Act(Deleted(s, "Вася"));

        ScenarioAssert.Accepted(s);
        Assert.Equal((DuelStatus.Cancelled, DuelEnd.AccountDeleted), (s.Duel(duel).Status, s.Duel(duel).End));
    }

    [Fact]
    public void Its_undo_brings_the_race_and_the_challenges_back_as_a_whole()
    {
        var s = DuelScenario.New(duels: d => d with { MaxOpenPerPlayer = 3 });
        var duel = s.StartDuel("Вася", "Петя");
        var challenge = s.Challenged("Маша", "Вася");
        var before = (s.Duel(duel), s.Invitation(challenge));
        s.Act(Deleted(s, "Вася"));
        ScenarioAssert.Accepted(s);

        s.Act(new UndoCommand(s.LastCommandId, "Аккаунт удалили по ошибке"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(before, (s.Duel(duel), s.Invitation(challenge)));
        Assert.Equal(SeasonEngine.Replay(s.EffectiveLog).Duels, s.State.Duels);
    }

    [Fact]
    public void The_undo_of_a_challenge_it_burned_waits_for_it()
    {
        var s = DuelScenario.New();
        s.Challenged("Маша", "Вася");
        var challenged = s.LastCommandId;
        s.Act(Deleted(s, "Вася"));
        ScenarioAssert.Accepted(s);
        var deleted = s.LastCommandId;

        Rejects(s, new UndoCommand(challenged, "Вызвала не того"), RejectionCodes.UndoDependents);
        Assert.Contains(deleted, s.Last.Rejection!.Related);
    }
}
