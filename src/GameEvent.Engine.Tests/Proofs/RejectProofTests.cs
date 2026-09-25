using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Proofs.ProofSetup;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// The admin rejects a run (W5; SPEC «Реджект: стандартный штраф — снимаются очки и клетки, полученные за это
/// прохождение. Остальные последствия … остаются»; D-15, D-98). <see cref="RejectProof"/> needs a comment and writes
/// <see cref="ProofRejected"/>, then <see cref="PointsChanged"/> of minus everything the run gave (the completion and
/// the corrections), <see cref="PlayerMoved"/> back by the run's net steps from the current cell along the walked path
/// (never past the start; none on the start), <see cref="CoinsChanged"/> of minus the run's coins, and «не применимо» for
/// the run's pending difficulty event — all with the <c>ProofRejected</c> reason and the run. The run becomes
/// <see cref="RunStatus.Rejected"/>; its game is not completed in the season any more and is available to everyone,
/// the same player included. First place and finishes are C9.
/// </summary>
public class RejectProofTests
{
    private static EquatableArray<string> Cells(int from, int to) =>
        [.. (from <= to ? Enumerable.Range(from, to - from + 1) : Enumerable.Range(to, from - to + 1).Reverse()).Select(i => $"c{i}")];

    // ---- What is taken back ----

    [Fact]
    public void Reject_takes_back_the_points_cells_and_coins_of_the_run()
    {
        // Given Вася completed a 6-hour game: 3 + 1 → 4 points, c4, 6 coins; he sent a proof
        var (s, runId) = Completed([3, 1]);
        var vasya = s.PlayerId("Вася");
        Submit(s, "Вася", runId, [Link], Note);
        var submittedAt = s.Clock.UtcNow;
        Assert.Equal((4, "c4", 6), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));

        // When the admin rejects it
        Reject(s, runId);

        // Then exactly: the rejection, minus 4 points, 4 cells back along the walked path, minus 6 coins
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [
                new ProofRejected(runId, vasya, Comment, s.Clock.UtcNow),
                new PointsChanged(vasya, -4, PointsReason.ProofRejected, runId),
                new PlayerMoved(vasya, "c4", LinearMap.StartId, -4, [.. Cells(3, 1), LinearMap.StartId], MoveReason.ProofRejected, runId),
                new CoinsChanged(vasya, -6, CoinsReason.ProofRejected, runId),
            ],
            s.Last.Events);

        // And the run is rejected, the proof keeps what was sent with the admin's comment
        var run = s.State.Runs[runId];
        Assert.Equal(RunStatus.Rejected, run.Status);
        Assert.Equal(new ProofState(ProofStatus.Rejected, [Link], Note, null, submittedAt, Comment), run.Proof);
        Assert.Equal((0, LinearMap.StartId, 0), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void Run_without_a_proof_can_be_rejected()
    {
        // The admin need not wait for a proof: an unchecked run is in the queue either way
        var (s, runId) = Completed([3, 1]);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Rejected, s.State.Runs[runId].Status);
        Assert.Equal(new ProofState(ProofStatus.Rejected, [], null, null, null, Comment), s.State.Runs[runId].Proof);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void Reject_takes_back_the_challenge_dice_too()
    {
        // 3 + 1 by hours and a challenge die 4 → 8 points
        var (s, runId) = Completed([3, 1, 4], ruleset: r => r with { Features = r.Features with { Challenges = true } }, challenge: true);
        Assert.Equal(8, s.Player("Вася").Points);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(-8, Assert.Single(s.LastEvents<PointsChanged>()).Delta);
        Assert.Equal(-8, Assert.Single(s.LastEvents<PlayerMoved>()).Steps);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void Rejecting_after_an_hours_correction_takes_back_the_corrected_totals()
    {
        // D-15: «с учётом последующих правок»: 6 → 12 hours adds dice 2 and 4 → 10 points, c10, 12 coins
        var (s, runId) = Completed([3, 1]);
        var vasya = s.PlayerId("Вася");
        s.NextRandom(2, 4);
        s.Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));
        ScenarioAssert.Accepted(s);
        Assert.Equal((10, "c10", 12), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(vasya, -10, PointsReason.ProofRejected, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(
            new PlayerMoved(vasya, "c10", LinearMap.StartId, -10, [.. Cells(9, 1), LinearMap.StartId], MoveReason.ProofRejected, runId),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(new CoinsChanged(vasya, -12, CoinsReason.ProofRejected, runId), Assert.Single(s.LastEvents<CoinsChanged>()));
        Assert.Equal((0, LinearMap.StartId, 0), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void Rejecting_after_a_difficulty_change_takes_back_what_is_left()
    {
        // hard 5 + 2 → 7; normal by the proof: 4 + 2 → 6 points, c6
        var (s, runId) = Completed([5, 2], Difficulty.Hard);
        s.Act(new ChangeRunDifficulty(runId, Difficulty.Normal, "По пруфу — нормальная"));
        ScenarioAssert.Accepted(s);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(-6, Assert.Single(s.LastEvents<PointsChanged>()).Delta);
        Assert.Equal(-6, Assert.Single(s.LastEvents<PlayerMoved>()).Steps);
        Assert.Equal((0, LinearMap.StartId), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Other_runs_keep_their_points_and_the_move_starts_from_the_current_cell()
    {
        // Given run A (3 + 1 → c4) and then run B (2 + 2 → c8), each with 6 coins
        var (s, runA) = Completed([3, 1]);
        var vasya = s.PlayerId("Вася");
        CompleteRun(s, "Вася", [2, 2]);
        Assert.Equal((8, "c8", 12), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));

        // When run A is rejected
        Reject(s, runA);

        // Then only A's 4 points, 4 cells from c8 and 6 coins go; B's stay
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(vasya, "c8", "c4", -4, Cells(7, 4), MoveReason.ProofRejected, runA),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal((4, "c4", 6), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
        Assert.All(s.State.Runs.Values.Where(r => r.RunId != runA), r => Assert.Equal(RunStatus.Completed, r.Status));
    }

    [Fact]
    public void Moving_back_retraces_the_walked_path()
    {
        var (s, runId) = Completed([3, 1]);
        CompleteRun(s, "Вася", [2, 2]);
        var pathBefore = s.Player("Вася").Path;

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(Movement.Backward(s.State.Map, pathBefore, 4), moved.Path);
        Assert.Equal(pathBefore.After(moved), s.Player("Вася").Path);
    }

    [Fact]
    public void Moving_back_is_clamped_at_the_start()
    {
        // Given the admin moved Вася to c2 after the run: 4 steps back reach the start after two
        var (s, runId) = Completed([3, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "c2"));
        ScenarioAssert.Accepted(s);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new PlayerMoved(s.PlayerId("Вася"), "c2", LinearMap.StartId, -4, ["c1", LinearMap.StartId], MoveReason.ProofRejected, runId),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal((0, LinearMap.StartId), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void On_the_start_the_reject_writes_no_move()
    {
        // D-47: a move that enters no cell writes no event
        var (s, runId) = Completed([3, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: LinearMap.StartId));
        ScenarioAssert.Accepted(s);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(ProofRejected), typeof(PointsChanged), typeof(CoinsChanged)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(LinearMap.StartId, s.Player("Вася").CellId);
    }

    [Fact]
    public void Coins_can_be_taken_below_zero()
    {
        // Вася spent his coins; the reject still takes back what the run gave
        var (s, runId) = Completed([3, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Купил", CoinsDelta: -6));
        ScenarioAssert.Accepted(s);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(-6, s.Player("Вася").Coins);
    }

    [Fact]
    public void Points_can_go_below_zero()
    {
        var (s, runId) = Completed([3, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Штраф", PointsDelta: -4));
        ScenarioAssert.Accepted(s);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(-4, s.Player("Вася").Points);
    }

    [Fact]
    public void Other_changes_of_the_player_stay()
    {
        // SPEC: «остальные последствия остаются»: an admin adjustment of points and coins is not the run's
        var (s, runId) = Completed([3, 1]);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Бонус", PointsDelta: 5, CoinsDelta: 2));
        ScenarioAssert.Accepted(s);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal((5, 2), (s.Player("Вася").Points, s.Player("Вася").Coins));
    }

    [Fact]
    public void Pending_difficulty_event_of_the_run_is_not_applicable()
    {
        // «Выше сложной» granted a good event that still waits: the run is gone, so the event is too
        var (s, runId) = Completed([5, 2], Difficulty.Extreme);
        var vasya = s.PlayerId("Вася");
        var effectId = Assert.Single(s.State.ManualEffects.Values).EffectId;

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(ProofRejected), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged), typeof(ManualEffectResolved)],
            s.Last.Events.Select(e => e.GetType()));
        var resolved = Assert.IsType<ManualEffectResolved>(s.Last.Events[^1]);
        Assert.Equal(
            (effectId, vasya, (Guid?)runId, ManualEffectOutcome.NotApplicable),
            (resolved.EffectId, resolved.PlayerId, resolved.RunId, resolved.Outcome));
        Assert.False(string.IsNullOrWhiteSpace(resolved.Comment));
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Other_pending_effects_stay()
    {
        // Вася's run B on «выше сложной» and a drop's bad event are not the rejected run's
        var (s, runA) = Completed([3, 1]);
        var runB = CompleteRun(s, "Вася", [2, 2], Difficulty.Extreme);
        DroppedRun(s);
        var pending = s.State.ManualEffects;
        Assert.Contains(pending.Values, e => e.RunId == runB);

        Reject(s, runA);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ManualEffectResolved>());
        Assert.Equal(pending, s.State.ManualEffects);
    }

    // ---- The game is available again (D-15) ----

    [Fact]
    public void Game_of_the_rejected_run_is_available_again_to_another_player()
    {
        // Given the pool has one game and Вася completed it: nobody can roll
        var (s, runId) = Completed([3, 1], onlyGame: true);
        var game = s.State.Runs[runId].GameId;
        s.ExpectRejection().Roll("Петя");
        Assert.Equal(RejectionCodes.NoAvailableGames, s.Last.Rejection!.Code);

        // When the run is rejected
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        // Then Петя rolls it, and it is not a «уже прошёл» miss
        s.Roll("Петя");
        var rolled = Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal(game, rolled.GameId);
        Assert.Empty(rolled.Misses);
    }

    [Fact]
    public void Game_of_the_rejected_run_is_available_again_to_the_same_player()
    {
        // D-15: «включая этого игрока» — a rejected run is not an exclusion like a drop
        var (s, runId) = Completed([3, 1], onlyGame: true);
        var game = s.State.Runs[runId].GameId;

        Reject(s, runId);
        ScenarioAssert.Accepted(s);
        s.Roll("Вася");

        Assert.Equal(game, Assert.Single(s.LastEvents<GameRolled>()).GameId);
        Assert.Empty(s.Player("Вася").Exclusions);
    }

    [Fact]
    public void Game_of_the_rejected_run_is_never_a_completed_in_season_miss()
    {
        // Two games: after the reject, whichever the wheel lands on, the rejected one is not «уже прошёл»
        var s = Scenario.New();
        s.WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror").WithPlayers("Вася", "Петя");
        var runId = CompleteRun(s, "Вася", [3, 1]);
        var game = s.State.Runs[runId].GameId;
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        for (var i = 0; i < 20; i++)
        {
            s.Roll("Петя");
            Assert.DoesNotContain(s.LastEvents<GameRolled>().Single().Misses, m => m.GameId == game && m.Reason == RollMissReason.CompletedInSeason);
            s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Сброс", DiscardOffer: true));
            ScenarioAssert.Accepted(s);
        }
    }

    // ---- Nobody else and nothing else ----

    [Fact]
    public void Reject_touches_only_the_owner_of_the_run()
    {
        var (s, runId) = Completed([3, 1]);
        CompleteRun(s, "Петя", [2, 2]);
        var petya = s.Player("Петя");

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.All(s.Last.Events.OfType<PointsChanged>(), e => Assert.Equal(s.PlayerId("Вася"), e.PlayerId));
        Assert.Equal(petya, s.Player("Петя"));
    }

    [Fact]
    public void Reject_leaves_the_owners_turn_alone()
    {
        var (s, runId) = Completed([3, 1]);
        s.Roll("Вася").Start("Вася");
        var active = s.Player("Вася").ActiveRunId;

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
        Assert.Equal(active, s.Player("Вася").ActiveRunId);
        Assert.Equal(RunStatus.Playing, s.State.Runs[active!.Value].Status);
    }

    // ---- Refusals ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_comment_is_rejected(string comment)
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, runId, comment), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Comment_over_the_limit_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, runId, new string('я', Limits.MaxCommentLength + 1)), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Rejecting_twice_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, runId), RejectionCodes.ProofAlreadyReviewed);
    }

    [Fact]
    public void Rejecting_an_approved_run_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link]);
        Approve(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, runId), RejectionCodes.ProofAlreadyReviewed);
    }

    [Fact]
    public void Run_being_played_is_rejected()
    {
        var (s, _) = Completed([3, 1]);
        s.Roll("Вася").Start("Вася");
        var playing = s.Player("Вася").ActiveRunId!.Value;

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, playing), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Dropped_run_is_rejected()
    {
        var (s, _) = Completed([3, 1]);
        var dropped = DroppedRun(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, dropped), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Unknown_run_is_rejected()
    {
        var (s, _) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, SequentialIds.Make(0x0BAD0000, 1)), RejectionCodes.RunUnknown);
    }

    [Fact]
    public void Reject_is_allowed_while_the_season_is_closing()
    {
        var (s, runId) = Completed([3, 1]);
        MoveSeasonTo(s, SeasonStatus.Closing);

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Reject_after_the_season_is_finished_is_rejected(SeasonStatus status)
    {
        var (s, runId) = Completed([3, 1]);
        s.MoveStatusToForcingFinish(status); // the run stays unchecked on purpose

        ScenarioAssert.RejectsWithoutChanges(s, x => Reject(x, runId), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void Rejected_run_cannot_be_corrected()
    {
        // D-98: a rejected run is not corrected; it comes back only by an undo (C12)
        var (s, runId) = Completed([3, 1]);
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new CorrectRunHours(runId, 12, "Часы")), RejectionCodes.RunNotCompleted);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "Пруф")), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Rejected_run_cannot_be_reviewed_later()
    {
        // A later review is only of a completed run (D-96): a rejected run is not completed any more
        var (s, runId) = Completed([3, 1]);
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Review("Вася", runId, 5), RejectionCodes.RunNotCompleted);
    }

    // ---- The log ----

    [Fact]
    public void Replaying_the_log_gives_the_same_state_after_a_reject()
    {
        var (s, runId) = Completed([5, 2], Difficulty.Extreme);
        s.NextRandom(1, 1);
        s.Act(new CorrectRunHours(runId, 12, "Часы"));
        Submit(s, "Вася", runId, [Link]);
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void Folding_the_reject_events_alone_updates_the_run_the_player_and_the_effects()
    {
        var (s, runId) = Completed([5, 2], Difficulty.Extreme);
        var before = s.State;

        Reject(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.State, s.Last.Events.Aggregate(before, SeasonEngine.Apply));
    }

    [Fact]
    public void Reject_uses_no_randomness()
    {
        var (s, runId) = Completed([3, 1]);

        var result = SeasonEngine.Execute(s.State, new RejectProof(runId, Comment), s.Context() with { Random = new Runs.NoRandom() });

        Assert.True(result.IsAccepted, $"Rejected: {result.Rejection}");
    }
}
