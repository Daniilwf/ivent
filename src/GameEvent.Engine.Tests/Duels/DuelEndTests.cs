using GameEvent.Engine.Duels;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Duels;

/// <summary>
/// EV30, EV63 (task 5.12, D-525, D-540, D-516): how a duel ends besides a win — a drop of a run that counts forfeits it
/// (<c>duels.dropForfeits</c>), a tech reroll calls it off, a reject of the winning run makes it a draw without passing the
/// win on, the timer burns a waiting challenge and draws a race, the deadline does both, a player marked inactive cancels it.
/// </summary>
public class DuelEndTests
{
    private const string Comment = "На скрине другая игра";

    // ---- A drop (D-540: dropForfeits) ----

    [Fact]
    public void Dropping_a_run_that_counts_gives_the_other_player_the_win_at_once()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        s.Play("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromHours(2));
        var vasyaCoins = s.Player("Вася").Coins;

        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        var now = s.Clock.UtcNow;
        Assert.Equal(
            new DuelSettled(duel, DuelStatus.Won, DuelEnd.Forfeit, s.PlayerId("Петя"), null, DuelScenario.RewardCoins, now),
            Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Equal(new CoinsChanged(s.PlayerId("Петя"), DuelScenario.RewardCoins, CoinsReason.Duel, null), Assert.Single(s.DuelCoins()));
        var won = s.Duel(duel);
        Assert.Equal((DuelStatus.Won, DuelEnd.Forfeit, (Guid?)s.PlayerId("Петя"), (Guid?)null), (won.Status, won.End, won.WinnerId, won.RunId));

        // The dropper pays nothing to the winner (EV41): his coins change only by the drop itself
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Вася") && c.Reason is CoinsReason.Duel or CoinsReason.DuelRevoked);
        Assert.Equal(vasyaCoins + s.LastEvents<CoinsChanged>().Where(c => c.PlayerId == s.PlayerId("Вася")).Sum(c => c.Delta), s.Player("Вася").Coins);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void Dropping_the_game_played_since_before_the_accept_changes_nothing()
    {
        var s = DuelScenario.New();
        s.Play("Вася", "Silent Hill");
        var duel = s.StartDuel("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(2));

        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    [Fact]
    public void Without_the_forfeit_rule_a_drop_changes_nothing()
    {
        var s = DuelScenario.New(d => d with { DropForfeits = false });
        var duel = s.StartDuel("Вася", "Петя");
        s.Play("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromHours(2));

        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    [Fact]
    public void A_drop_of_a_third_player_changes_nothing()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        s.Play("Маша", "Silent Hill");
        s.Advance(TimeSpan.FromHours(2));

        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Маша")));

        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    // ---- A tech reroll (D-540: nobody's fault) ----

    [Fact]
    public void A_tech_reroll_of_a_run_that_counts_calls_the_duel_off()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        s.Play("Петя", "Doom 3");

        s.NextRandom(0, 0).Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.DoesNotLaunch, null));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new DuelSettled(duel, DuelStatus.Cancelled, DuelEnd.TechReroll, null, null, 0, s.Clock.UtcNow),
            Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Empty(s.DuelCoins());
        Assert.Equal((DuelStatus.Cancelled, (DuelEnd?)DuelEnd.TechReroll), (s.Duel(duel).Status, s.Duel(duel).End));
    }

    [Fact]
    public void A_tech_reroll_of_the_game_played_since_before_the_accept_changes_nothing()
    {
        var s = DuelScenario.New();
        s.Play("Петя", "Doom 3");
        var duel = s.StartDuel("Вася", "Петя");

        s.NextRandom(0, 0).Act(new TechReroll(s.PlayerId("Петя"), TechRerollReason.DoesNotLaunch, null));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    // ---- A reject of the winning run (the expert panel (5), D-414) ----

    public static TheoryData<string> Rejects() => ["plain", "withDropPenalty"];

    private static void Reject(Scenario s, string how, Guid run)
    {
        if (how == "plain")
        {
            s.Act(new RejectProof(run, Comment));
        }
        else
        {
            s.NextRandom(1, 1).Act(new RejectProofWithDropPenalty(run, Comment));
        }

        ScenarioAssert.Accepted(s);
    }

    [Theory]
    [MemberData(nameof(Rejects))]
    public void A_reject_of_the_winning_run_makes_the_duel_a_draw_and_takes_the_reward_back(string how)
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill");
        var petyaCoins = s.Player("Петя").Coins;

        Reject(s, how, run);

        Assert.Equal(new DuelWinRevoked(duel, s.PlayerId("Вася"), run, DuelScenario.RewardCoins), Assert.Single(s.LastEvents<DuelWinRevoked>()));
        var back = Assert.Single(s.DuelCoins());
        Assert.Equal((s.PlayerId("Вася"), -DuelScenario.RewardCoins, CoinsReason.DuelRevoked), (back.PlayerId, back.Delta, back.Reason));
        var drawn = s.Duel(duel);
        Assert.Equal((DuelStatus.Drawn, (DuelEnd?)DuelEnd.WinRejected), (drawn.Status, drawn.End));

        // The win does not pass to the other player
        Assert.Empty(s.LastEvents<DuelSettled>());
        Assert.NotEqual((Guid?)s.PlayerId("Петя"), drawn.WinnerId);
        Assert.Equal(petyaCoins, s.Player("Петя").Coins);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void A_reject_of_another_run_of_the_winner_changes_nothing()
    {
        var s = DuelScenario.New();
        var other = s.CompleteTitle("Вася", "Doom 3");
        var duel = s.StartDuel("Вася", "Петя");
        s.CompleteTitle("Вася", "Silent Hill");
        var won = s.Duel(duel);

        s.Act(new RejectProof(other, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.DuelEvents());
        Assert.Empty(s.DuelCoins());
        Assert.Equal(won, s.Duel(duel));
    }

    [Fact]
    public void A_reject_leaves_the_frozen_first_finisher_his_coins()
    {
        // As a bet won by the frozen first (D-422 (2)): the win is revoked, the coins stay
        var s = DuelScenario.New(more: r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }, mapLength: 4);
        var duel = s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill"); // 2 cells, the duel won with its coins
        Assert.Equal(DuelScenario.RewardCoins, Assert.Single(s.LastEvents<DuelSettled>()).Coins);
        s.CompleteTitle("Вася", "Doom 3"); // 2 more: the finish, frozen at once
        Assert.True(s.Player("Вася").Finish!.Frozen);
        var coins = s.Player("Вася").Coins;

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new DuelWinRevoked(duel, s.PlayerId("Вася"), run, 0), Assert.Single(s.LastEvents<DuelWinRevoked>()));
        Assert.Empty(s.DuelCoins());
        Assert.Equal(coins, s.Player("Вася").Coins);
        Assert.Equal(DuelStatus.Drawn, s.Duel(duel).Status);
    }

    [Fact]
    public void A_forfeit_win_is_not_touched_by_rejects()
    {
        var s = DuelScenario.New();
        var petyaEarly = s.Play("Петя", "Doom 3");
        var duel = s.StartDuel("Вася", "Петя");
        s.Play("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromHours(2));
        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Вася")));
        var won = s.Duel(duel);
        Assert.Equal(DuelEnd.Forfeit, won.End);
        s.CompleteActive("Петя");

        s.Act(new RejectProof(petyaEarly, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.DuelEvents());
        Assert.Empty(s.DuelCoins());
        Assert.Equal(won, s.Duel(duel));
    }

    // ---- The timer (D-523: interactions.inviteTtlHours; D-525: duels.durationDays) ----

    [Fact]
    public void The_timer_burns_a_challenge_left_without_an_answer()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        var expiresAt = s.Duel(duel).ExpiresAt;
        Assert.Equal(expiresAt, DuelTimers.Next(s.State, s.PlayerId("Вася")));

        s.Advance(TimeSpan.FromHours(DuelScenario.InviteTtlHours) - TimeSpan.FromSeconds(1));
        s.FireTimers();
        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Pending, s.Duel(duel).Status);

        s.Advance(TimeSpan.FromSeconds(1));
        s.FireTimers();

        ScenarioAssert.Accepted(s);
        Assert.Equal(new DuelExpired(duel, DuelExpiry.Timeout, s.Clock.UtcNow), Assert.Single(s.LastEvents<DuelExpired>()));
        Assert.Equal(DuelStatus.Expired, s.Duel(duel).Status);
        Assert.Null(DuelTimers.Next(s.State, s.PlayerId("Вася")));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void The_timer_draws_a_race_nobody_won_in_time()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var end = s.Clock.UtcNow.AddDays(DuelScenario.DurationDays);
        Assert.Equal(end, DuelTimers.Next(s.State, s.PlayerId("Вася")));

        s.Advance(TimeSpan.FromDays(DuelScenario.DurationDays) - TimeSpan.FromSeconds(1));
        s.FireTimers();
        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);

        s.Advance(TimeSpan.FromSeconds(1));
        s.FireTimers();

        ScenarioAssert.Accepted(s);
        Assert.Equal(new DuelSettled(duel, DuelStatus.Drawn, DuelEnd.TimeOut, null, null, 0, s.Clock.UtcNow), Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Empty(s.DuelCoins());
        Assert.Equal(DuelStatus.Drawn, s.Duel(duel).Status);
        Assert.Null(DuelTimers.Next(s.State, s.PlayerId("Вася")));
    }

    [Fact]
    public void The_next_timer_of_a_challenger_is_the_earliest_end_of_his_open_duels()
    {
        var s = DuelScenario.New(d => d with { MaxOpenPerPlayer = 2 });
        Assert.Null(DuelTimers.Next(s.State, s.PlayerId("Вася")));
        var race = s.StartDuel("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(1));
        var waiting = s.Challenged("Вася", "Маша");

        Assert.Equal(s.Duel(waiting).ExpiresAt, DuelTimers.Next(s.State, s.PlayerId("Вася")));
        Assert.True(s.Duel(waiting).ExpiresAt < s.Duel(race).ExpiresAt);
    }

    [Fact]
    public void A_won_duel_has_no_timer()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        s.CompleteTitle("Петя", "Doom 3");

        Assert.Null(DuelTimers.Next(s.State, s.PlayerId("Вася")));
        s.Advance(TimeSpan.FromDays(DuelScenario.DurationDays + 1));
        s.FireTimers();
        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Won, s.Duel(duel).Status);
    }

    // ---- The deadline (D-516) ----

    [Fact]
    public void Closing_the_season_burns_the_waiting_challenges_and_draws_the_races()
    {
        var s = DuelScenario.New();
        var waiting = s.Challenged("Вася", "Петя");
        var race = s.StartDuel("Маша", "Коля");

        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        ScenarioAssert.Accepted(s);
        var now = s.Clock.UtcNow;
        Assert.Equal(new DuelExpired(waiting, DuelExpiry.Deadline, now), Assert.Single(s.LastEvents<DuelExpired>()));
        Assert.Equal(new DuelSettled(race, DuelStatus.Drawn, DuelEnd.Deadline, null, null, 0, now), Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Empty(s.DuelCoins());
        Assert.DoesNotContain(s.State.Duels.Values, d => d.IsOpen);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void Reaching_the_deadline_ends_the_duels_too()
    {
        var s = DuelScenario.New();
        var waiting = s.Challenged("Вася", "Петя");
        var race = s.StartDuel("Маша", "Коля");
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(2)));
        s.Advance(TimeSpan.FromHours(2));

        s.Act(new ReachDeadline());

        ScenarioAssert.Accepted(s);
        Assert.Equal(DuelExpiry.Deadline, Assert.Single(s.LastEvents<DuelExpired>()).Reason);
        Assert.Equal((DuelStatus.Drawn, DuelEnd.Deadline), (Assert.Single(s.LastEvents<DuelSettled>()).Status, s.LastEvents<DuelSettled>().Single().End));
        Assert.Equal((DuelStatus.Expired, DuelStatus.Drawn), (s.Duel(waiting).Status, s.Duel(race).Status));
    }

    [Fact]
    public void A_win_before_the_deadline_stays_and_its_proof_holds_the_results()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill");
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        Assert.Empty(s.DuelEvents());

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<DuelWinRevoked>());
        Assert.Equal(DuelStatus.Drawn, s.Duel(duel).Status);
    }

    // ---- A player marked inactive ----

    [Theory]
    [InlineData("Вася")]
    [InlineData("Петя")]
    public void Marking_either_player_inactive_burns_the_challenge(string inactive)
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");

        s.Act(new SetPlayerInactive(s.PlayerId(inactive), true));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new DuelExpired(duel, DuelExpiry.Inactive, s.Clock.UtcNow), Assert.Single(s.LastEvents<DuelExpired>()));
        Assert.Equal(DuelStatus.Expired, s.Duel(duel).Status);
    }

    [Theory]
    [InlineData("Вася")]
    [InlineData("Петя")]
    public void Marking_either_player_inactive_cancels_the_race(string inactive)
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");

        s.Act(new SetPlayerInactive(s.PlayerId(inactive), true));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new DuelSettled(duel, DuelStatus.Cancelled, DuelEnd.Inactive, null, null, 0, s.Clock.UtcNow), Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Empty(s.DuelCoins());
        Assert.Equal(DuelStatus.Cancelled, s.Duel(duel).Status);
    }

    [Fact]
    public void Marking_a_third_player_inactive_changes_nothing()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");

        s.Act(new SetPlayerInactive(s.PlayerId("Маша"), true));

        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }
}
