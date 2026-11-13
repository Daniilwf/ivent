using GameEvent.Engine.Duels;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Duels;

/// <summary>
/// Duels after the rules audit and the code review (task 5.12, D-732…D-735): undos wait for commands that read a duel's
/// standing, the codes of an accept over a limit name the right player, coop and duels on together close each invitation
/// once, a revoke of the first finish takes the new first out of the race, a wish reroll forfeits.
/// </summary>
public class DuelReviewTests
{
    private const string Comment = "Ошибка";

    // ---- Undo: the duel standing of a player is read by later commands (D-735, invariant 12) ----

    [Fact]
    public void Undoing_the_win_waits_for_a_later_completion_of_the_other_player_in_the_race()
    {
        // Without the win Петя's fresh completion would have won: the undo must not bring a race back he can no longer win
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        s.Play("Петя", "Doom 3");
        s.CompleteTitle("Вася", "Silent Hill");
        var won = s.LastCommandId;
        s.CompleteActive("Петя");
        var later = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(won, Comment));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(later, s.Last.Rejection!.Related);
    }

    [Fact]
    public void Undoing_the_win_waits_for_a_later_challenge_to_the_winner()
    {
        // The challenge was decided by the winner's open duels and wins: without the win he would have two open duels
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        s.CompleteTitle("Вася", "Silent Hill");
        var won = s.LastCommandId;
        s.Challenged("Маша", "Вася");
        var challenged = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(won, Comment));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(challenged, s.Last.Rejection!.Related);
    }

    [Fact]
    public void A_completion_of_a_player_outside_any_race_does_not_hold_back_the_undo_of_a_win()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        s.CompleteTitle("Вася", "Silent Hill");
        var won = s.LastCommandId;
        s.CompleteTitle("Маша", "Doom 3");

        s.Act(new UndoCommand(won, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(SeasonEngine.Replay(s.EffectiveLog).Duels, s.State.Duels);
    }

    // ---- The codes of an accept over a limit name the right player (D-732) ----

    [Fact]
    public void An_accept_over_the_accepting_players_own_limit_is_too_many()
    {
        var s = DuelScenario.New();
        var waiting = s.Challenged("Петя", "Маша");
        s.StartDuel("Коля", "Маша");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Answer("Маша", waiting, accept: true), RejectionCodes.DuelTooMany);
    }

    [Fact]
    public void An_accept_over_the_challengers_limit_is_the_opponent_busy()
    {
        // The challenger can be over the limit only when the limit went down since his challenge
        var s = DuelScenario.New(d => d with { MaxOpenPerPlayer = 2 });
        var waiting = s.Challenged("Петя", "Маша");
        s.StartDuel("Петя", "Вася");
        s.Act(new ChangeRuleset(s.State.Rules with { Duels = s.State.Rules.Duels! with { MaxOpenPerPlayer = 1 } }));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Answer("Маша", waiting, accept: true), RejectionCodes.DuelOpponentBusy);
    }

    // ---- Coop and duels on together (D-718, D-730) ----

    private static Scenario Both() => DuelScenario.New(more: r => r.WithCoop());

    [Fact]
    public void With_coop_on_too_closing_the_season_burns_a_challenge_once()
    {
        var s = Both();
        var waiting = s.Challenged("Вася", "Петя");

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new InteractionExpired(waiting, InteractionExpiry.Deadline, s.Clock.UtcNow), Assert.Single(s.LastEvents<InteractionExpired>()));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void With_coop_on_too_the_timer_burns_a_challenge_once()
    {
        var s = Both();
        var waiting = s.Challenged("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(DuelScenario.InviteTtlHours));

        s.FireTimers();

        Assert.Equal(new InteractionExpired(waiting, InteractionExpiry.Timeout, s.Clock.UtcNow), Assert.Single(s.LastEvents<InteractionExpired>()));
    }

    [Fact]
    public void With_coop_on_too_a_challenge_is_answered_and_withdrawn_by_the_duels_rules()
    {
        var s = Both();
        var accepted = s.Challenged("Вася", "Петя");
        s.Accepted("Петя", accepted);
        Assert.Equal(DuelStatus.Active, s.Duel(accepted).Status);

        var withdrawn = s.Challenged("Маша", "Коля");
        s.Withdraw("Маша", withdrawn);
        ScenarioAssert.Accepted(s);
        Assert.Equal(InteractionStatus.Withdrawn, s.Invitation(withdrawn).Status);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new AnswerInvitation(x.PlayerId("Коля"), Guid.NewGuid(), true)), RejectionCodes.InteractionUnknown);
    }

    [Fact]
    public void A_duels_challenge_is_not_answered_by_coop_once_duels_are_off()
    {
        var s = Both();
        var waiting = s.Challenged("Вася", "Петя");
        s.AppendCraftedEvents(new RulesetChanged(s.State.RulesetVersion + 1, s.State.Rules with { Features = s.State.Rules.Features with { Duels = false } }));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Answer("Петя", waiting, accept: true), RejectionCodes.FeatureDisabled);
    }

    // ---- The first finisher after a revoke of the first finish (D-734, EV42) ----

    [Fact]
    public void Who_becomes_first_after_a_revoke_of_the_first_finish_leaves_the_race()
    {
        var s = DuelScenario.New(d => d with { MaxOpenPerPlayer = 2 }, mapLength: 2);
        var first = s.CompleteTitle("Вася", "Silent Hill");
        s.Play("Петя", "Doom 3");
        var race = s.StartDuel("Петя", "Маша"); // Doom 3 was played before the accept: its completion does not win
        s.CompleteActive("Петя");
        Assert.Equal(DuelStatus.Active, s.Duel(race).Status);
        var waiting = s.Challenged("Коля", "Петя");

        s.Act(new RejectProof(first, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.PlayerId("Петя"), FinishLine.First(s.State));
        var now = s.Clock.UtcNow;
        Assert.Equal(new DuelSettled(race, DuelStatus.Cancelled, DuelEnd.FirstFinisher, null, null, 0, now), Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Equal(new InteractionExpired(waiting, InteractionExpiry.FirstFinisher, now), Assert.Single(s.LastEvents<InteractionExpired>()));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    // ---- Wish rerolls and tech rerolls turned into drops (D-733) ----

    private static Scenario WithWish(Func<DuelRules, DuelRules>? duels = null) =>
        DuelScenario.New(duels, more: r => r with { Roll = r.Roll with { WishRerollTags = ["Horror"] } });

    [Fact]
    public void A_wish_reroll_of_a_run_that_counts_forfeits_like_a_drop()
    {
        var s = WithWish();
        var duel = s.StartDuel("Вася", "Петя");
        s.Play("Петя", "Doom 3");

        s.NextRandom(0, 0).Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.Wish, null));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new DuelSettled(duel, DuelStatus.Won, DuelEnd.Forfeit, s.PlayerId("Вася"), null, DuelScenario.RewardCoins, s.Clock.UtcNow),
            Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Equal(DuelScenario.RewardCoins, Assert.Single(s.DuelCoins()).Delta);
    }

    [Fact]
    public void A_wish_reroll_changes_nothing_when_drops_do_not_forfeit()
    {
        var s = WithWish(d => d with { DropForfeits = false });
        var duel = s.StartDuel("Вася", "Петя");
        s.Play("Петя", "Doom 3");

        s.NextRandom(0, 0).Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.Wish, null));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<DuelSettled>());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    [Fact]
    public void Turning_the_tech_reroll_into_a_drop_leaves_the_called_off_duel_as_it_is()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var run = s.Play("Петя", "Doom 3");
        s.NextRandom(0, 0).Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.DoesNotLaunch, null));
        var called = s.Duel(duel);
        Assert.Equal(DuelStatus.Cancelled, called.Status);

        s.NextRandom(1, 1).Act(new ConvertTechRerollToDrop(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<DuelSettled>());
        Assert.Empty(s.DuelCoins());
        Assert.Equal(called, s.Duel(duel));
    }
}
