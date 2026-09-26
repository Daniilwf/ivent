using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Proofs.ProofSetup;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// The limit of unchecked runs (D-134, the customer's «вариант 2, N = 2»): <c>season.maxUncheckedRuns</c> (null — no
/// limit). A run is unchecked when it is completed and its proof is not approved, sent (pending) or not; approved runs
/// (with or without a proof), rejected, dropped and tech-rerolled runs do not count, nor do the runs the frozen first plays
/// in free mode. A new roll from Idle (<see cref="RollGame"/>) is refused with <c>roll.tooManyUnchecked</c> while the
/// player has that many or more; nothing else is blocked — the turn already started goes on (reroll, «Уже проходил»,
/// start, tech reroll, drop, completion) and proofs can be sent. An approval or a reject that brings the count under the
/// limit allows the roll again. Test ruleset: 6-hour games give two d4 on normal; a linear map of 60 steps.
/// </summary>
public class UncheckedLimitTests
{
    private const string WithoutProof = "Видел на стриме";

    private static readonly string[] s_titles =
    [
        "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma", "Prey", "Doom", "Quake", "Portal",
    ];

    private static Func<Ruleset, Ruleset> Limit(int? limit) =>
        r => r with { Season = r.Season with { MaxUncheckedRuns = limit } };

    /// <summary>Вася, Петя and Маша in a running season with ten 6-hour horror games and the given limit.</summary>
    private static Scenario Season(int? limit, Func<Ruleset, Ruleset>? more = null)
    {
        var s = Scenario.New().WithRuleset(Limit(limit));
        if (more is not null)
        {
            s.WithRuleset(more);
        }

        s.WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        return s.WithPlayers("Вася", "Петя", "Маша");
    }

    /// <summary>The player completes <paramref name="count"/> runs without sending a proof (1 + 1 each: 2 points, 2 cells).</summary>
    private static List<Guid> CompleteRuns(Scenario s, string player, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => CompleteRun(s, player, [1, 1]))];

    private static void RollIsRefused(Scenario s, string player = "Вася") =>
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll(player), RejectionCodes.TooManyUncheckedRuns);

    private static void RollIsAccepted(Scenario s, string player = "Вася")
    {
        s.Roll(player);
        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Rolling, s.Player(player).Phase);
    }

    [Fact]
    public void Rejection_code_is_the_agreed_one()
    {
        Assert.Equal("roll.tooManyUnchecked", RejectionCodes.TooManyUncheckedRuns);
    }

    // ---- The limit is reached ----

    [Fact]
    public void Two_completed_runs_without_a_proof_block_the_next_roll()
    {
        // Given the limit is 2 and Вася completed two runs, no proof sent
        var s = Season(2);
        CompleteRuns(s, "Вася", 2);

        // When he rolls again, then the roll is refused and nothing changes; he stays Idle
        RollIsRefused(s);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        Assert.Null(s.Player("Вася").Offer);
    }

    [Fact]
    public void Refused_roll_writes_no_events_and_keeps_the_state()
    {
        var s = Season(2);
        CompleteRuns(s, "Вася", 2);
        var before = s.State;
        var log = s.Log.Count;
        s.Advance(TimeSpan.FromHours(5));

        s.ExpectRejection().Roll("Вася");

        Assert.False(s.Last.IsAccepted);
        Assert.Equal(RejectionCodes.TooManyUncheckedRuns, s.Last.Rejection!.Code);
        Assert.Empty(s.Last.Events);
        Assert.Equal(before, s.State);
        Assert.Equal(log, s.Log.Count);
    }

    [Fact]
    public void One_unchecked_run_under_a_limit_of_two_does_not_block()
    {
        var s = Season(2);
        CompleteRuns(s, "Вася", 1);

        RollIsAccepted(s);
    }

    [Fact]
    public void Pending_proofs_still_count()
    {
        // Given both runs have a proof sent but not checked yet
        var s = Season(2);
        var runs = CompleteRuns(s, "Вася", 2);
        foreach (var run in runs)
        {
            Submit(s, "Вася", run, [Link], Note);
            ScenarioAssert.Accepted(s);
        }

        Assert.All(runs, r => Assert.Equal(ProofStatus.Pending, s.State.Runs[r].Proof!.Status));

        // Then the roll is still refused
        RollIsRefused(s);
    }

    [Fact]
    public void Limit_of_one_blocks_after_a_single_unchecked_run()
    {
        var s = Season(1);
        CompleteRuns(s, "Вася", 1);

        RollIsRefused(s);
    }

    [Fact]
    public void First_roll_of_the_season_is_allowed_with_a_limit_of_one()
    {
        var s = Season(1);

        RollIsAccepted(s);
    }

    [Fact]
    public void More_unchecked_runs_than_the_limit_block_as_well()
    {
        // Given three unchecked runs (the limit was lowered to 2 after they were played)
        var s = Season(null);
        var runs = CompleteRuns(s, "Вася", 3);
        s.WithRuleset(Limit(2));

        // Then the roll is refused; one approval leaves two — still refused; a second leaves one — allowed
        RollIsRefused(s);
        Approve(s, runs[0], comment: WithoutProof);
        ScenarioAssert.Accepted(s);
        RollIsRefused(s);
        Approve(s, runs[1], comment: WithoutProof);
        ScenarioAssert.Accepted(s);
        RollIsAccepted(s);
    }

    // ---- The admin's check frees a slot ----

    [Fact]
    public void Approving_one_of_two_allows_the_roll_again()
    {
        var s = Season(2);
        var runs = CompleteRuns(s, "Вася", 2);
        Submit(s, "Вася", runs[0], [Link]);
        RollIsRefused(s);

        Approve(s, runs[0]);
        ScenarioAssert.Accepted(s);

        RollIsAccepted(s);
    }

    [Fact]
    public void Approving_without_a_proof_frees_a_slot()
    {
        var s = Season(2);
        var runs = CompleteRuns(s, "Вася", 2);
        RollIsRefused(s);

        Approve(s, runs[1], comment: WithoutProof);
        ScenarioAssert.Accepted(s);
        Assert.Equal(ProofStatus.Approved, s.State.Runs[runs[1]].Proof!.Status);

        RollIsAccepted(s);
    }

    [Fact]
    public void Rejecting_one_of_two_allows_the_roll_again()
    {
        var s = Season(2);
        var runs = CompleteRuns(s, "Вася", 2);
        Submit(s, "Вася", runs[0], [Link]);
        RollIsRefused(s);

        Reject(s, runs[0]);
        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Rejected, s.State.Runs[runs[0]].Status);

        RollIsAccepted(s);
    }

    [Fact]
    public void Rejecting_a_run_without_a_proof_frees_a_slot_too()
    {
        var s = Season(1);
        var run = Assert.Single(CompleteRuns(s, "Вася", 1));
        RollIsRefused(s);

        Reject(s, run);
        ScenarioAssert.Accepted(s);

        RollIsAccepted(s);
    }

    [Fact]
    public void Undoing_the_approval_blocks_the_roll_again()
    {
        // The count follows the state: the approval undone makes the run unchecked again
        var s = Season(1);
        var run = Assert.Single(CompleteRuns(s, "Вася", 1));
        Approve(s, run, comment: WithoutProof);
        ScenarioAssert.Accepted(s);
        var approval = s.LastCommandId;

        s.Act(new UndoCommand(approval, "ошибка админа"));
        ScenarioAssert.Accepted(s);

        RollIsRefused(s);
    }

    // ---- What does not count ----

    [Fact]
    public void Dropped_runs_do_not_count()
    {
        var s = Season(1);
        DroppedRun(s);
        DroppedRun(s);

        RollIsAccepted(s);
    }

    [Fact]
    public void Tech_rerolled_runs_do_not_count()
    {
        // Given the limit is 2: Вася tech-rerolls a game, then completes the new one
        var s = Season(2);
        s.Roll("Вася").Start("Вася");
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);
        s.Start("Вася");
        s.NextRandom(1, 1).Complete("Вася");
        ScenarioAssert.Accepted(s);

        // Then only the completed run counts: one of two
        RollIsAccepted(s);
    }

    [Fact]
    public void Other_players_runs_do_not_count()
    {
        // Given Петя is at the limit and Вася has one unchecked run of two
        var s = Season(2);
        CompleteRuns(s, "Петя", 2);
        CompleteRuns(s, "Вася", 1);

        // Then Вася rolls, Петя does not
        RollIsAccepted(s, "Вася");
        RollIsRefused(s, "Петя");
    }

    // ---- No limit ----

    [Fact]
    public void Null_limit_allows_any_number_of_unchecked_runs()
    {
        var s = Season(null);
        CompleteRuns(s, "Вася", 6);

        RollIsAccepted(s);
    }

    [Fact]
    public void Ruleset_without_the_field_has_no_limit()
    {
        // The pinned test ruleset predates the field (an old season): no limit
        var s = Scenario.New().WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        s.WithPlayers("Вася");
        Assert.Null(s.Ruleset.Season.MaxUncheckedRuns);
        CompleteRuns(s, "Вася", 4);

        RollIsAccepted(s);
    }

    [Fact]
    public void Removing_the_limit_mid_season_allows_the_roll()
    {
        var s = Season(2);
        CompleteRuns(s, "Вася", 2);
        RollIsRefused(s);

        s.WithRuleset(Limit(null));

        RollIsAccepted(s);
    }

    // ---- Only the next roll is blocked ----

    [Fact]
    public void Completing_the_run_that_reaches_the_limit_is_allowed()
    {
        // Given one unchecked run of two: Вася rolls and starts the next game
        var s = Season(2);
        CompleteRuns(s, "Вася", 1);
        s.Roll("Вася").Start("Вася");
        var run = s.Player("Вася").ActiveRunId!.Value;

        // When he completes it — the second unchecked run
        s.NextRandom(1, 1).Complete("Вася");

        // Then the completion counts as usual; only the next roll is refused
        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Completed, s.State.Runs[run].Status);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        RollIsRefused(s);
    }

    [Fact]
    public void Proofs_can_be_submitted_while_the_roll_is_blocked()
    {
        var s = Season(2);
        var runs = CompleteRuns(s, "Вася", 2);
        RollIsRefused(s);

        Submit(s, "Вася", runs[0], [Link], Note);
        ScenarioAssert.Accepted(s);
        Submit(s, "Вася", runs[1], [], witness: s.PlayerId("Петя"));
        ScenarioAssert.Accepted(s);

        // A sent proof is still unchecked: the roll waits for the admin
        RollIsRefused(s);
    }

    /// <summary>
    /// Вася has two unchecked runs and an offer rolled while there was no limit; then the admin sets the limit to 2.
    /// </summary>
    private static Scenario BlockedWithAnOffer(Func<Ruleset, Ruleset>? more = null)
    {
        var s = Season(null, more);
        CompleteRuns(s, "Вася", 2);
        RollIsAccepted(s);
        s.WithRuleset(Limit(2));
        return s;
    }

    [Fact]
    public void Reroll_inside_a_started_turn_is_not_blocked()
    {
        var s = BlockedWithAnOffer();
        var offered = s.Player("Вася").Offer!.GameId;

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
        Assert.NotEqual(offered, s.Player("Вася").Offer!.GameId);
    }

    [Fact]
    public void Already_played_inside_a_started_turn_is_not_blocked()
    {
        var s = BlockedWithAnOffer();
        var offered = s.Player("Вася").Offer!.GameId;

        s.Act(new DeclareAlreadyPlayed(s.PlayerId("Вася"), offered));

        ScenarioAssert.Accepted(s);
        Assert.Contains(s.Last.Events, e => e is GameRolled);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
    }

    [Fact]
    public void Start_and_tech_reroll_inside_a_started_turn_are_not_blocked()
    {
        var s = BlockedWithAnOffer();

        s.Start("Вася");
        ScenarioAssert.Accepted(s);
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));

        // The tech reroll rolls a new game at once, inside the same turn
        ScenarioAssert.Accepted(s);
        Assert.Contains(s.Last.Events, e => e is GameRolled);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
    }

    [Fact]
    public void Drop_inside_a_started_turn_is_not_blocked_and_the_next_roll_is()
    {
        var s = BlockedWithAnOffer();
        s.Start("Вася");
        s.Advance(TimeSpan.FromHours(2));

        s.Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        RollIsRefused(s);
    }

    [Fact]
    public void Completion_inside_a_started_turn_is_not_blocked()
    {
        var s = BlockedWithAnOffer();
        s.Start("Вася");

        s.NextRandom(1, 1).Complete("Вася");

        ScenarioAssert.Accepted(s);
        RollIsRefused(s);
    }

    [Fact]
    public void Roll_with_a_choice_of_games_is_blocked_too()
    {
        var s = Season(2, r => r with { Roll = r.Roll with { ChoiceCount = 3 } });
        for (var i = 0; i < 2; i++)
        {
            s.Roll("Вася");
            var choice = s.Player("Вася").Choice!;
            // Picking a game starts it in the same command (D-91)
            s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id));
            ScenarioAssert.Accepted(s);
            s.NextRandom(1, 1).Complete("Вася");
            ScenarioAssert.Accepted(s);
            s.Advance(TimeSpan.FromHours(1));
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Вася"), RejectionCodes.TooManyUncheckedRuns);
        Assert.Null(s.Player("Вася").Choice);
    }

    // ---- The first finisher's free mode ----

    /// <summary>A four-step map: from the start 3 + 1 reach the finish.</summary>
    private static Scenario ShortMap(int limit)
    {
        var s = Scenario.New().WithRuleset(Limit(limit)).WithMapLength(4).WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        return s.WithPlayers("Вася", "Петя", "Маша");
    }

    [Fact]
    public void Runs_of_the_frozen_first_in_free_mode_do_not_count()
    {
        // Given Вася finished first and the admin approved it: he is frozen and plays on in free mode
        var s = ShortMap(1);
        var finishing = CompleteRun(s, "Вася", [3, 1]);
        Approve(s, finishing, comment: WithoutProof);
        ScenarioAssert.Accepted(s);
        Assert.True(s.Player("Вася").Finish!.Frozen);

        // When he completes two more runs nobody checks
        CompleteRuns(s, "Вася", 2);

        // Then his roll is not limited
        RollIsAccepted(s);
    }

    [Fact]
    public void Provisional_first_finish_counts_until_it_is_approved()
    {
        // The finishing run is an unchecked completed run like any other; it was not played in free mode
        var s = ShortMap(1);
        var finishing = CompleteRun(s, "Вася", [3, 1]);
        Assert.Equal(1, s.Player("Вася").Finish!.Order);
        Assert.False(s.Player("Вася").Finish!.Frozen);

        RollIsRefused(s);

        Approve(s, finishing, comment: WithoutProof);
        ScenarioAssert.Accepted(s);
        RollIsAccepted(s);
    }

    [Fact]
    public void Runs_of_a_later_finisher_still_count()
    {
        // Given Вася is the frozen first; Петя finishes second with an unchecked run
        var s = ShortMap(1);
        Approve(s, CompleteRun(s, "Вася", [3, 1]), comment: WithoutProof);
        ScenarioAssert.Accepted(s);
        CompleteRun(s, "Петя", [3, 1]);
        Assert.Equal(2, s.Player("Петя").Finish!.Order);

        // Then Петя's roll is limited: only the first's free mode is exempt
        RollIsRefused(s, "Петя");
    }

    [Fact]
    public void Runs_of_a_provisional_first_after_his_finish_count_too()
    {
        // D-134: only the frozen first is exempt; a provisional first still gains points in free mode, so his runs wait
        var s = ShortMap(2);
        CompleteRun(s, "Вася", [3, 1]);
        Assert.False(s.Player("Вася").Finish!.Frozen);
        RollIsAccepted(s);
        s.Start("Вася");
        ScenarioAssert.Accepted(s);
        s.Complete("Вася", Difficulty.Normal);
        ScenarioAssert.Accepted(s);
        Assert.Contains(s.State.Runs.Values, r => r.PlayerId == s.PlayerId("Вася") && r.FreeMode);

        RollIsRefused(s);
    }

    [Fact]
    public void A_revoked_first_keeps_counting_his_free_mode_runs()
    {
        // Given Вася finished (provisionally) and played one more run in free mode
        var s = ShortMap(2);
        var finishing = CompleteRun(s, "Вася", [3, 1]);
        RollIsAccepted(s);
        s.Start("Вася");
        s.Complete("Вася", Difficulty.Normal);
        ScenarioAssert.Accepted(s);

        // When the finishing run is rejected, the finish is revoked; the free-mode run still waits
        Reject(s, finishing);
        ScenarioAssert.Accepted(s);
        Assert.Null(s.Player("Вася").Finish);
        s.WithRuleset(Limit(1));

        RollIsRefused(s);
    }

    [Fact]
    public void The_frozen_first_is_never_held_even_by_runs_from_before_his_finish()
    {
        // Given the first freezes at once (no approval required) with two unchecked runs behind him
        var s = Scenario.New()
            .WithRuleset(Limit(2))
            .WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } })
            .WithMapLength(4)
            .WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        s.WithPlayers("Вася", "Петя", "Маша");
        CompleteRun(s, "Вася", [1, 1]);
        CompleteRun(s, "Вася", [1, 1]);
        Assert.True(s.Player("Вася").Finish!.Frozen);

        // Then a check would take nothing from him: his roll is free
        RollIsAccepted(s);
    }

    [Fact]
    public void Undoing_a_reject_blocks_the_roll_again()
    {
        var s = Season(1);
        var run = Assert.Single(CompleteRuns(s, "Вася", 1));
        Reject(s, run);
        ScenarioAssert.Accepted(s);
        var reject = s.LastCommandId;

        s.Act(new UndoCommand(reject, "ошибка админа"));
        ScenarioAssert.Accepted(s);

        RollIsRefused(s);
    }

    [Fact]
    public void A_limit_set_through_a_rule_change_holds_the_next_roll()
    {
        // Given no limit and two unchecked runs
        var s = Season(null);
        CompleteRuns(s, "Вася", 2);

        // When the admin sets the limit to 2 in the season's rules
        s.Act(new ChangeRuleset(Limit(2)(s.Ruleset)));
        ScenarioAssert.Accepted(s);

        // Then the next roll waits
        RollIsRefused(s);
    }
}
