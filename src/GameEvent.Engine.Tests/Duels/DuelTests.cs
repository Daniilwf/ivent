using GameEvent.Engine.Duels;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Duels;

/// <summary>
/// EV30, EV41, EV63 (task 5.12, D-523, D-525, D-540): a challenge waits for its answer, an accept starts a race to the next
/// completion of a run rolled after it, the winner gets <c>duels.rewardCoins</c> from the system and the loser loses
/// nothing; one open duel a player, a cooldown of the pair, a limit of wins a season; correcting hours later does not
/// re-decide the duel; the flag off refuses everything.
/// </summary>
public class DuelTests
{
    private static Ruleset Off(Scenario s) => s.Ruleset with { Features = s.Ruleset.Features with { Duels = false } };

    // ---- The challenge (D-523) ----

    [Fact]
    public void A_challenge_waits_for_the_answer_for_the_invite_time()
    {
        var s = DuelScenario.New();
        var now = s.Clock.UtcNow;

        s.Challenge("Вася", "Петя");

        ScenarioAssert.Accepted(s);
        var duel = Assert.Single(s.LastEvents<DuelChallenged>()).Duel;
        Assert.Equal(
            (s.PlayerId("Вася"), s.PlayerId("Петя"), DuelStatus.Pending, now, now.AddHours(DuelScenario.InviteTtlHours)),
            (duel.ChallengerId, duel.OpponentId, duel.Status, duel.CreatedAt, duel.ExpiresAt));
        Assert.Equal((null, null, null, 0), (duel.AcceptedAt, duel.WinnerId, duel.RunId, duel.Coins));
        Assert.Equal(duel, s.Duel(duel.DuelId));
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void An_accept_starts_the_race_and_remembers_the_runs_the_two_were_playing()
    {
        var s = DuelScenario.New();
        var vasyaRun = s.Play("Вася", "Silent Hill");
        var duel = s.Challenged("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(3));
        var now = s.Clock.UtcNow;

        s.Answer("Петя", duel, accept: true);

        ScenarioAssert.Accepted(s);
        var answered = Assert.Single(s.LastEvents<DuelAnswered>());
        Assert.Equal((duel, true, now, (DateTimeOffset?)now.AddDays(DuelScenario.DurationDays)), (answered.DuelId, answered.Accepted, answered.AnsweredAt, answered.ExpiresAt));
        Assert.Equal([vasyaRun], answered.RunsAtAcceptance.ToArray());
        var state = s.Duel(duel);
        Assert.Equal((DuelStatus.Active, (DateTimeOffset?)now, now.AddDays(DuelScenario.DurationDays)), (state.Status, state.AcceptedAt, state.ExpiresAt));
        Assert.Equal([vasyaRun], state.RunsAtAcceptance.ToArray());
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void The_runs_at_acceptance_are_those_of_both_players()
    {
        var s = DuelScenario.New();
        var vasyaRun = s.Play("Вася", "Silent Hill");
        var petyaRun = s.Play("Петя", "Doom 3");
        s.Play("Маша", "Alan Wake");

        var duel = s.StartDuel("Вася", "Петя");

        Assert.Equal(new[] { vasyaRun, petyaRun }.Order(), s.Duel(duel).RunsAtAcceptance.Order());
    }

    [Fact]
    public void A_decline_closes_the_challenge_without_a_race()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        var now = s.Clock.UtcNow;

        s.Answer("Петя", duel, accept: false);

        ScenarioAssert.Accepted(s);
        var answered = Assert.Single(s.LastEvents<DuelAnswered>());
        Assert.Equal((duel, false, now, (DateTimeOffset?)null), (answered.DuelId, answered.Accepted, answered.AnsweredAt, answered.ExpiresAt));
        Assert.Empty(answered.RunsAtAcceptance);
        Assert.Equal(DuelStatus.Declined, s.Duel(duel).Status);
        Assert.Null(s.Duel(duel).AcceptedAt);
    }

    [Fact]
    public void The_challenger_withdraws_his_waiting_challenge()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(1));

        s.Withdraw("Вася", duel);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new DuelWithdrawn(duel, s.Clock.UtcNow), Assert.Single(s.LastEvents<DuelWithdrawn>()));
        Assert.Equal(DuelStatus.Withdrawn, s.Duel(duel).Status);
    }

    [Theory]
    [InlineData("self", RejectionCodes.DuelOnSelf)]
    [InlineData("unknown", RejectionCodes.PlayerUnknown)]
    [InlineData("inactiveChallenger", RejectionCodes.DuelPlayerInactive)]
    [InlineData("inactiveOpponent", RejectionCodes.DuelPlayerInactive)]
    [InlineData("samePending", RejectionCodes.DuelAlreadyOpen)]
    [InlineData("reversePending", RejectionCodes.DuelAlreadyOpen)]
    [InlineData("active", RejectionCodes.DuelAlreadyOpen)]
    [InlineData("sentPending", RejectionCodes.DuelTooMany)]
    [InlineData("activeAsOpponent", RejectionCodes.DuelTooMany)]
    [InlineData("opponentSent", RejectionCodes.DuelOpponentBusy)]
    [InlineData("opponentActive", RejectionCodes.DuelOpponentBusy)]
    public void A_challenge_is_refused_when_the_rules_do_not_allow_it(string what, string code)
    {
        var s = DuelScenario.New();
        var opponent = s.PlayerId("Петя");
        switch (what)
        {
            case "self": opponent = s.PlayerId("Вася"); break;
            case "unknown": opponent = SequentialIds.Make(0x10000000, 99); break;
            case "inactiveChallenger": s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true)); break;
            case "inactiveOpponent": s.Act(new SetPlayerInactive(s.PlayerId("Петя"), true)); break;
            case "samePending": s.Challenged("Вася", "Петя"); break;
            case "reversePending": s.Challenged("Петя", "Вася"); break;
            case "active": s.StartDuel("Петя", "Вася"); break;
            case "sentPending": s.Challenged("Вася", "Маша"); break;
            case "activeAsOpponent": s.StartDuel("Маша", "Вася"); break;
            case "opponentSent": s.Challenged("Петя", "Маша"); break;
            case "opponentActive": s.StartDuel("Маша", "Петя"); break;
            default: break;
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChallengeToDuel(x.PlayerId("Вася"), opponent)), code);
    }

    [Fact]
    public void Challenges_waiting_for_a_players_answer_do_not_count_against_his_limit()
    {
        var s = DuelScenario.New();
        s.Challenged("Петя", "Маша");
        s.Challenged("Коля", "Маша");

        s.Challenge("Вася", "Маша");

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, s.State.Duels.Values.Count(d => d.OpponentId == s.PlayerId("Маша") && d.Status == DuelStatus.Pending));
    }

    [Fact]
    public void A_raised_limit_allows_more_open_duels()
    {
        var s = DuelScenario.New(d => d with { MaxOpenPerPlayer = 2 });
        s.StartDuel("Вася", "Петя");

        s.Challenge("Вася", "Маша");
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Challenge("Вася", "Коля"), RejectionCodes.DuelTooMany);
    }

    [Fact]
    public void A_challenge_closed_without_a_race_frees_the_place()
    {
        var s = DuelScenario.New();
        var declined = s.Challenged("Вася", "Петя");
        s.Answer("Петя", declined, accept: false);
        var withdrawn = s.Challenged("Вася", "Маша");
        s.Withdraw("Вася", withdrawn);

        s.Challenge("Вася", "Коля");

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_challenge_is_refused_while_the_flag_is_off()
    {
        var s = Scenario.New().WithMapLength(60).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChallengeToDuel(x.PlayerId("Вася"), x.PlayerId("Петя"))), RejectionCodes.FeatureDisabled);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new AnswerDuel(x.PlayerId("Петя"), SequentialIds.Make(0x7D000000, 1), true)), RejectionCodes.FeatureDisabled);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new WithdrawDuel(x.PlayerId("Вася"), SequentialIds.Make(0x7D000000, 1))), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void With_the_flag_off_a_season_writes_no_duel_events()
    {
        var s = Scenario.New().WithMapLength(60).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Doom 3", 6, "Horror").WithPlayers("Вася", "Петя");
        s.Act(new ChallengeToDuel(s.PlayerId("Вася"), s.PlayerId("Петя")));
        s.CompleteTitle("Вася", "Silent Hill");
        s.Play("Петя", "Doom 3");
        s.Advance(TimeSpan.FromDays(8));
        s.Act(new Engine.Economy.FireTimers());
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        Assert.DoesNotContain(s.Log, e => e is DuelChallenged or DuelAnswered or DuelWithdrawn or DuelExpired or DuelSettled or DuelWinRevoked);
        Assert.Empty(s.State.Duels);
        Assert.Null(DuelTimers.Next(s.State, s.PlayerId("Вася")));
    }

    // ---- The answer and the withdrawal (D-523) ----

    [Theory]
    [InlineData("unknown", RejectionCodes.DuelUnknown)]
    [InlineData("challenger", RejectionCodes.DuelNotYours)]
    [InlineData("third", RejectionCodes.DuelNotYours)]
    [InlineData("answered", RejectionCodes.DuelNotPending)]
    [InlineData("declined", RejectionCodes.DuelNotPending)]
    [InlineData("withdrawn", RejectionCodes.DuelNotPending)]
    [InlineData("expired", RejectionCodes.DuelExpired)]
    public void An_answer_is_refused_when_it_is_not_the_opponents_or_the_challenge_does_not_wait(string what, string code)
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        var player = "Петя";
        switch (what)
        {
            case "unknown": duel = SequentialIds.Make(0x7D000000, 99); break;
            case "challenger": player = "Вася"; break;
            case "third": player = "Маша"; break;
            case "answered": s.Accepted("Петя", duel); break;
            case "declined": s.Answer("Петя", duel, accept: false); break;
            case "withdrawn": s.Withdraw("Вася", duel); break;
            case "expired": s.Advance(TimeSpan.FromHours(DuelScenario.InviteTtlHours)); break;
            default: break;
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Answer(player, duel, accept: true), code);
    }

    [Fact]
    public void A_challenge_past_its_time_stays_waiting_until_the_timer_closes_it()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(DuelScenario.InviteTtlHours) + TimeSpan.FromMinutes(5));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Answer("Петя", duel, accept: false), RejectionCodes.DuelExpired);

        Assert.Equal(DuelStatus.Pending, s.Duel(duel).Status);
    }

    [Fact]
    public void An_answer_a_second_before_the_challenge_burns_is_taken()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(DuelScenario.InviteTtlHours) - TimeSpan.FromSeconds(1));

        s.Answer("Петя", duel, accept: true);

        ScenarioAssert.Accepted(s);
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    [Theory]
    [InlineData("opponent", RejectionCodes.DuelNotYours)]
    [InlineData("third", RejectionCodes.DuelNotYours)]
    [InlineData("accepted", RejectionCodes.DuelNotPending)]
    [InlineData("twice", RejectionCodes.DuelNotPending)]
    [InlineData("unknown", RejectionCodes.DuelUnknown)]
    public void Only_the_challenger_withdraws_and_only_a_waiting_challenge(string what, string code)
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        var player = "Вася";
        switch (what)
        {
            case "opponent": player = "Петя"; break;
            case "third": player = "Маша"; break;
            case "accepted": s.Accepted("Петя", duel); break;
            case "twice": s.Withdraw("Вася", duel); break;
            case "unknown": duel = SequentialIds.Make(0x7D000000, 99); break;
            default: break;
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Withdraw(player, duel), code);
    }

    [Fact]
    public void An_accept_checks_the_limits_again_without_this_duel()
    {
        // Маша's incoming challenge did not count; by the time she answers she has accepted another duel
        var s = DuelScenario.New();
        var waiting = s.Challenged("Петя", "Маша");
        s.StartDuel("Коля", "Маша");

        s.ExpectRejection().Answer("Маша", waiting, accept: true);

        Assert.False(s.Last.IsAccepted);
        Assert.Contains(s.Last.Rejection!.Code, new[] { RejectionCodes.DuelTooMany, RejectionCodes.DuelOpponentBusy });
        Assert.Equal(DuelStatus.Pending, s.Duel(waiting).Status);
    }

    [Fact]
    public void An_accept_is_refused_once_the_opponent_is_at_the_win_limit()
    {
        var s = DuelScenario.New(d => d with { MaxWinsPerSeason = 1 });
        var waiting = s.Challenged("Петя", "Маша");
        s.StartDuel("Маша", "Вася");
        s.CompleteTitle("Маша", "Silent Hill");
        Assert.Equal(DuelStatus.Won, Assert.Single(s.LastEvents<DuelSettled>()).Status);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Answer("Маша", waiting, accept: true), RejectionCodes.DuelWinLimit);
    }

    [Fact]
    public void An_accept_is_refused_once_the_challenger_is_inactive()
    {
        // Marking him inactive closes the challenge, so the answer finds it closed
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        s.Act(new SetPlayerInactive(s.PlayerId("Вася"), true));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Answer("Петя", duel, accept: true), RejectionCodes.DuelNotPending);
    }

    // ---- The race: whoever first completes a run that counts (D-525, EV30) ----

    [Fact]
    public void The_first_completion_after_the_accept_wins_the_reward_from_the_system()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(1));
        var run = s.Play("Вася", "Silent Hill");
        s.Advance(TimeSpan.FromDays(2));
        var petyaCoins = s.Player("Петя").Coins;

        s.NextRandom(1, 1).Complete("Вася");

        var now = s.Clock.UtcNow;
        Assert.Equal(
            new DuelSettled(duel, DuelStatus.Won, DuelEnd.Completed, s.PlayerId("Вася"), run, DuelScenario.RewardCoins, now),
            Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Equal(new CoinsChanged(s.PlayerId("Вася"), DuelScenario.RewardCoins, CoinsReason.Duel, run), Assert.Single(s.DuelCoins()));
        var events = s.Last.Events.ToList();
        Assert.True(
            events.FindIndex(e => e is DuelSettled) < events.FindIndex(e => e is CoinsChanged { Reason: CoinsReason.Duel }),
            "The settlement comes before its coins.");
        var won = s.Duel(duel);
        Assert.Equal(
            (DuelStatus.Won, DuelEnd.Completed, (Guid?)s.PlayerId("Вася"), (Guid?)run, DuelScenario.RewardCoins, (DateTimeOffset?)now),
            (won.Status, won.End, won.WinnerId, won.RunId, won.Coins, won.EndedAt));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        // EV41: the loser gets nothing and loses nothing — no coins pass between the two
        Assert.DoesNotContain(s.Last.Events.OfType<CoinsChanged>(), c => c.PlayerId == s.PlayerId("Петя"));
        Assert.Equal(petyaCoins, s.Player("Петя").Coins);
    }

    [Fact]
    public void The_challenger_and_the_opponent_race_alike()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");

        var run = s.CompleteTitle("Петя", "Doom 3");

        var settled = Assert.Single(s.LastEvents<DuelSettled>());
        Assert.Equal((duel, DuelStatus.Won, (Guid?)s.PlayerId("Петя"), (Guid?)run), (settled.DuelId, settled.Status, settled.WinnerId, settled.RunId));
        Assert.Equal(DuelScenario.RewardCoins, Assert.Single(s.DuelCoins()).Delta);
    }

    [Fact]
    public void The_completion_pays_its_own_coins_and_the_duel_adds_exactly_its_reward()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        var before = s.Player("Вася").Coins;

        s.CompleteTitle("Вася", "Silent Hill");

        var all = s.LastEvents<CoinsChanged>().Where(c => c.PlayerId == s.PlayerId("Вася")).ToList();
        Assert.Equal(before + all.Sum(c => c.Delta), s.Player("Вася").Coins);
        Assert.Equal(DuelScenario.RewardCoins, all.Where(c => c.Reason == CoinsReason.Duel).Sum(c => c.Delta));
        Assert.Contains(all, c => c.Reason != CoinsReason.Duel);
    }

    [Fact]
    public void The_game_a_player_was_playing_when_he_accepted_never_wins()
    {
        // EV63: no sniping with a game nearly done (duels.countRunsStartedAfterAccept)
        var s = DuelScenario.New();
        var early = s.Play("Петя", "Doom 3");
        var duel = s.StartDuel("Вася", "Петя");

        s.CompleteActive("Петя");

        Assert.Empty(s.DuelEvents());
        Assert.Empty(s.DuelCoins());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
        Assert.Contains(early, s.Duel(duel).RunsAtAcceptance);

        var fresh = s.CompleteTitle("Петя", "Alan Wake");
        var settled = Assert.Single(s.LastEvents<DuelSettled>());
        Assert.Equal(((Guid?)s.PlayerId("Петя"), (Guid?)fresh), (settled.WinnerId, settled.RunId));
    }

    [Fact]
    public void A_game_rolled_before_the_accept_does_not_win_even_if_started_after()
    {
        var s = DuelScenario.New();
        s.RollTitle("Петя", "Doom 3");
        var duel = s.StartDuel("Вася", "Петя");
        s.Advance(TimeSpan.FromHours(1));
        s.Start("Петя");

        s.CompleteActive("Петя");

        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    [Fact]
    public void Without_the_freshness_rule_the_game_already_played_wins_too()
    {
        var s = DuelScenario.New(d => d with { CountRunsStartedAfterAccept = false });
        var early = s.Play("Петя", "Doom 3");
        var duel = s.StartDuel("Вася", "Петя");

        s.CompleteActive("Петя");

        var settled = Assert.Single(s.LastEvents<DuelSettled>());
        Assert.Equal((duel, DuelStatus.Won, (Guid?)s.PlayerId("Петя"), (Guid?)early), (settled.DuelId, settled.Status, settled.WinnerId, settled.RunId));
    }

    [Fact]
    public void A_later_completion_of_the_other_player_changes_nothing()
    {
        var s = DuelScenario.New();
        s.Play("Петя", "Doom 3");
        var duel = s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill");
        var won = s.Duel(duel);

        s.CompleteActive("Петя");
        s.CompleteTitle("Петя", "Alan Wake");

        Assert.Empty(s.DuelEvents());
        Assert.Empty(s.DuelCoins());
        Assert.Equal(won, s.Duel(duel));
        Assert.Equal((Guid?)run, s.Duel(duel).RunId);
    }

    [Fact]
    public void A_completion_of_a_third_player_changes_nothing()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");

        s.CompleteTitle("Маша", "Silent Hill");

        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public void A_completion_wins_only_before_the_race_ends(int secondsFromEnd, bool wins)
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        s.Play("Вася", "Silent Hill");
        s.Advance(s.Duel(duel).ExpiresAt - s.Clock.UtcNow + TimeSpan.FromSeconds(secondsFromEnd));

        s.CompleteActive("Вася");

        Assert.Equal(wins, s.LastEvents<DuelSettled>().Any(d => d.Status == DuelStatus.Won));
        Assert.Equal(wins ? DuelScenario.RewardCoins : 0, s.DuelCoins().Sum(c => c.Delta));
    }

    [Fact]
    public void Correcting_the_hours_of_the_winning_run_does_not_redecide_the_duel()
    {
        // EV63: the outcome stays as the queue decided it
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill");
        var won = s.Duel(duel);

        s.NextRandom(1, 1).Act(new CorrectRunHours(run, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.DuelEvents());
        Assert.Empty(s.DuelCoins());
        Assert.Equal(won, s.Duel(duel));
    }

    [Fact]
    public void Approving_the_winning_run_changes_nothing()
    {
        var s = DuelScenario.New();
        var duel = s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill");
        var won = s.Duel(duel);

        s.Act(new ApproveProof(run, null, "Видел на стриме"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.DuelEvents());
        Assert.Equal(won, s.Duel(duel));
    }

    [Fact]
    public void An_ended_duel_frees_the_place_for_another()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        s.CompleteTitle("Вася", "Silent Hill");

        s.Challenge("Вася", "Маша");

        ScenarioAssert.Accepted(s);
    }

    // ---- Against farming: the pair's cooldown and the season's win limit (D-540, EV63) ----

    [Theory]
    [InlineData("Вася", "Петя")]
    [InlineData("Петя", "Вася")]
    public void The_same_pair_waits_out_the_cooldown_after_their_duel(string challenger, string opponent)
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        s.CompleteTitle("Вася", "Silent Hill");
        var ended = s.Clock.UtcNow;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Challenge(challenger, opponent), RejectionCodes.DuelCooldown);

        s.Advance(ended.AddDays(DuelScenario.CooldownDays) - s.Clock.UtcNow - TimeSpan.FromSeconds(1));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Challenge(challenger, opponent), RejectionCodes.DuelCooldown);

        s.Advance(TimeSpan.FromSeconds(1));
        s.Challenge(challenger, opponent);
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_drawn_duel_starts_the_cooldown_too()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        s.Advance(TimeSpan.FromDays(DuelScenario.DurationDays));
        s.Act(new Engine.Economy.FireTimers());

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Challenge("Петя", "Вася"), RejectionCodes.DuelCooldown);
    }

    [Fact]
    public void A_challenge_that_was_never_accepted_starts_no_cooldown()
    {
        var s = DuelScenario.New();
        var declined = s.Challenged("Вася", "Петя");
        s.Answer("Петя", declined, accept: false);
        var withdrawn = s.Challenged("Петя", "Вася");
        s.Withdraw("Петя", withdrawn);

        s.Challenge("Вася", "Петя");

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Other_pairs_are_not_held_by_a_cooldown()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        s.CompleteTitle("Вася", "Silent Hill");

        s.Challenge("Петя", "Маша");

        ScenarioAssert.Accepted(s);
    }

    [Theory]
    [InlineData("Вася", "Маша")]
    [InlineData("Маша", "Вася")]
    public void A_player_at_the_win_limit_neither_challenges_nor_is_challenged(string challenger, string opponent)
    {
        var s = DuelScenario.New(d => d with { MaxWinsPerSeason = 1 });
        s.StartDuel("Вася", "Петя");
        s.CompleteTitle("Вася", "Silent Hill");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Challenge(challenger, opponent), RejectionCodes.DuelWinLimit);

        // The loser is not held
        s.Challenge("Петя", "Маша");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_revoked_win_no_longer_counts_against_the_limit()
    {
        var s = DuelScenario.New(d => d with { MaxWinsPerSeason = 1 });
        s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill");
        s.Act(new RejectProof(run, "На скрине другая игра"));

        s.Challenge("Вася", "Маша");

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_win_past_the_limit_pays_nothing()
    {
        // Two races won by one completion: the second win is past maxWinsPerSeason = 1
        var s = DuelScenario.New(d => d with { MaxWinsPerSeason = 1, MaxOpenPerPlayer = 2 });
        s.StartDuel("Вася", "Петя");
        s.StartDuel("Вася", "Маша");

        s.CompleteTitle("Вася", "Silent Hill");

        var settled = s.LastEvents<DuelSettled>().ToList();
        Assert.Equal(2, settled.Count);
        Assert.All(settled, d => Assert.Equal((DuelStatus.Won, (Guid?)s.PlayerId("Вася")), (d.Status, d.WinnerId)));
        Assert.Equal(new[] { 0, DuelScenario.RewardCoins }, settled.Select(d => d.Coins).Order());
        Assert.Equal(DuelScenario.RewardCoins, Assert.Single(s.DuelCoins()).Delta);
    }

    // ---- The season's state (D-516) ----

    [Fact]
    public void A_challenge_is_refused_at_the_deadline_of_a_season_still_active()
    {
        var s = DuelScenario.New();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromDays(2)));
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromDays(2));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Challenge("Вася", "Петя"), RejectionCodes.SeasonDeadlinePassed);
    }

    [Fact]
    public void Nothing_of_a_duel_is_done_after_the_deadline()
    {
        var s = DuelScenario.New();
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));

        s.Challenge("Вася", "Петя");

        Assert.False(s.Last.IsAccepted);
        Assert.Contains(s.Last.Rejection!.Code, new[] { RejectionCodes.SeasonDeadlinePassed, RejectionCodes.SeasonNotActive });
        Assert.Empty(s.State.Duels);
    }

    // ---- The flag (D-420, D-730) ----

    [Fact]
    public void The_flag_is_not_switched_off_while_a_challenge_waits()
    {
        var s = DuelScenario.New();
        s.Challenged("Вася", "Петя");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(Off(x))), RejectionCodes.DuelsInPlay);
    }

    [Fact]
    public void The_flag_is_not_switched_off_while_a_race_goes_on()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(Off(x))), RejectionCodes.DuelsInPlay);
    }

    [Fact]
    public void The_flag_waits_for_the_proof_of_the_winning_run()
    {
        var s = DuelScenario.New();
        s.StartDuel("Вася", "Петя");
        var run = s.CompleteTitle("Вася", "Silent Hill");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(Off(x))), RejectionCodes.DuelsInPlay);

        s.Act(new ApproveProof(run, null, "Видел на стриме"));
        s.Act(new ChangeRuleset(Off(s)));

        ScenarioAssert.Accepted(s);
        Assert.False(s.State.Rules.Features.Duels);
    }

    [Fact]
    public void The_flag_is_switched_off_once_no_duel_is_in_play()
    {
        var s = DuelScenario.New();
        var duel = s.Challenged("Вася", "Петя");
        s.Answer("Петя", duel, accept: false);

        s.Act(new ChangeRuleset(Off(s)));

        ScenarioAssert.Accepted(s);
        Assert.Null(DuelTimers.Next(s.State, s.PlayerId("Вася")));
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Challenge("Вася", "Маша"), RejectionCodes.FeatureDisabled);
    }

    // ---- The defaults and the validator (D-540) ----

    [Fact]
    public void The_default_rules_hold_the_expert_panels_numbers_with_the_flag_off()
    {
        var rules = RulesetJson.Default();

        Assert.False(rules.Features.Duels);
        var duels = Assert.IsType<DuelRules>(rules.Duels);
        Assert.Equal(
            (10, 7, 1, true, 14, 5, true),
            (duels.RewardCoins, duels.DurationDays, duels.MaxOpenPerPlayer, duels.CountRunsStartedAfterAccept, duels.SamePairCooldownDays, duels.MaxWinsPerSeason, duels.DropForfeits));
        Assert.Equal(24, rules.Interactions.InviteTtlHours);
    }

    [Fact]
    public void The_duel_rules_are_playable()
    {
        Assert.Empty(RulesetValidator.Validate(TestRuleset.Create().WithDuels()));
    }

    [Fact]
    public void The_flag_without_the_duel_rules_is_invalid()
    {
        var rules = TestRuleset.Create() with { Features = TestRuleset.Create().Features with { Duels = true }, Duels = null };

        Assert.Contains(RulesetValidator.Validate(rules), e => e.Path == "duels");
    }

    public static TheoryData<string, Func<DuelRules, DuelRules>, string> OutOfRange() => new()
    {
        { "a race of no days", d => d with { DurationDays = 0 }, "duels.durationDays" },
        { "negative coins", d => d with { RewardCoins = -1 }, "duels.rewardCoins" },
        { "no open duel", d => d with { MaxOpenPerPlayer = 0 }, "duels.maxOpenPerPlayer" },
        { "negative cooldown", d => d with { SamePairCooldownDays = -1 }, "duels.samePairCooldownDays" },
        { "no win a season", d => d with { MaxWinsPerSeason = 0 }, "duels.maxWinsPerSeason" },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Duel_numbers_out_of_range_are_invalid(string what, Func<DuelRules, DuelRules> change, string path)
    {
        _ = what;
        var errors = RulesetValidator.Validate(TestRuleset.Create().WithDuels(change));

        Assert.Equal(path, Assert.Single(errors).Path);
    }
}
