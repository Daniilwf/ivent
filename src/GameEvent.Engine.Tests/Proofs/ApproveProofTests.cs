using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Proofs.ProofSetup;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// The admin approves a run (W4, W8; SPEC «Очередь пруфов: одобрить, отклонить, одобрить без скрина»; «Сложность
/// засчитывается по пруфу. Если игрок понизил сложность посреди игры, считается более низкая»; Q-5, D-97, D-98).
/// <see cref="ApproveProof"/> with a submitted proof writes <see cref="ProofApproved"/> (without proof: false); without
/// one — «одобрить без скрина» — a comment is required (without proof: true). A difficulty below the claimed one needs a
/// comment too (D-98 (3), no default text in the engine) and is a difficulty change in the same command: <see cref="RunDifficultyChanged"/> and the difference (points, move, the
/// pending difficulty event «не применимо»), then <see cref="ProofApproved"/>; a higher one is refused. Only a completed,
/// unchecked run; until the season is finished. Corrections stay possible after the approval.
/// </summary>
public class ApproveProofTests
{
    private const string WithoutProof = "Видел на стриме";
    private const string LowerByProof = "На скрине нормальная сложность";

    private static Guid PendingEffectOf(Scenario s, Guid runId) =>
        Assert.Single(s.State.ManualEffects.Values, e => e.RunId == runId && e.Source == ManualEffectSource.Difficulty).EffectId;

    // ---- With a proof ----

    [Fact]
    public void Admin_approves_a_submitted_proof()
    {
        // Given Вася completed (4 points, c4) and sent a proof
        var (s, runId) = Completed([3, 1]);
        var vasya = s.PlayerId("Вася");
        Submit(s, "Вася", runId, [Link], Note, s.PlayerId("Петя"));
        var submittedAt = s.Clock.UtcNow;
        s.Advance(TimeSpan.FromHours(3));
        var before = s.State;

        // When the admin approves it
        Approve(s, runId);

        // Then exactly one event; the proof keeps what was sent and is approved
        ScenarioAssert.Accepted(s);
        Assert.Equal([new ProofApproved(runId, vasya, false, null, s.Clock.UtcNow)], s.Last.Events);
        var run = s.State.Runs[runId];
        Assert.Equal(new ProofState(ProofStatus.Approved, [Link], Note, s.PlayerId("Петя"), submittedAt, null), run.Proof);
        Assert.Equal(RunStatus.Completed, run.Status);

        // And the dice already counted: points, cells and coins stay (W4)
        Assert.Equal(before.Players, s.State.Players);
    }

    [Fact]
    public void Approval_with_a_proof_may_carry_a_comment()
    {
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link]);

        Approve(s, runId, comment: "Титры видно");

        ScenarioAssert.Accepted(s);
        Assert.Equal(new ProofApproved(runId, s.PlayerId("Вася"), false, "Титры видно", s.Clock.UtcNow), Assert.Single(s.Last.Events));
        Assert.Equal("Титры видно", s.State.Runs[runId].Proof!.Comment);
    }

    [Fact]
    public void Proof_with_a_witness_only_is_approved_as_a_proof()
    {
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [], witness: s.PlayerId("Маша"));

        Approve(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.False(Assert.Single(s.LastEvents<ProofApproved>()).WithoutProof);
    }

    // ---- Without a proof («одобрить без скрина») ----

    [Fact]
    public void Admin_approves_a_run_without_a_proof_with_a_comment()
    {
        var (s, runId) = Completed([3, 1]);
        var before = s.State.Players;

        Approve(s, runId, comment: WithoutProof);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new ProofApproved(runId, s.PlayerId("Вася"), true, WithoutProof, s.Clock.UtcNow)], s.Last.Events);
        Assert.Equal(new ProofState(ProofStatus.Approved, [], null, null, null, WithoutProof), s.State.Runs[runId].Proof);
        Assert.Equal(before, s.State.Players);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Approval_without_a_proof_needs_a_comment(string? comment)
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, runId, comment: comment), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Comment_over_the_limit_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Approve(x, runId, comment: new string('я', Limits.MaxCommentLength + 1)), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void Comment_at_the_limit_is_accepted()
    {
        var (s, runId) = Completed([3, 1]);
        var comment = new string('я', Limits.MaxCommentLength);

        Approve(s, runId, comment: comment);

        ScenarioAssert.Accepted(s);
        Assert.Equal(comment, Assert.Single(s.LastEvents<ProofApproved>()).Comment);
    }

    // ---- Difficulty by the proof (Q-5) ----

    [Fact]
    public void Lower_difficulty_by_the_proof_changes_the_run_in_the_same_command()
    {
        // Given Вася claimed hard: d6 showing 5 and 2 → 7 points, c7; his proof shows normal
        var (s, runId) = Completed([5, 2], Difficulty.Hard);
        var vasya = s.PlayerId("Вася");
        Submit(s, "Вася", runId, [Link]);

        // When the admin approves at normal with the reason (D-98 (3): a lower difficulty needs a comment)
        Approve(s, runId, Difficulty.Normal, LowerByProof);

        // Then the difficulty change and its difference come first, the approval last
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunDifficultyChanged), typeof(PointsChanged), typeof(PlayerMoved), typeof(ProofApproved)],
            s.Last.Events.Select(e => e.GetType()));
        var changed = Assert.IsType<RunDifficultyChanged>(s.Last.Events[0]);
        Assert.Equal(
            (runId, vasya, Difficulty.Hard, Difficulty.Normal, s.Clock.UtcNow),
            (changed.RunId, changed.PlayerId, changed.OldDifficulty, changed.NewDifficulty, changed.ChangedAt));
        Assert.Equal([new DieChange(new Die(6, 5), new Die(4, 4)), new DieChange(new Die(6, 2), new Die(4, 2))], changed.Dice);
        Assert.Equal(LowerByProof, changed.Comment);
        Assert.Equal(new PointsChanged(vasya, -1, PointsReason.RunCorrection, runId), s.Last.Events[1]);
        Assert.Equal(new PlayerMoved(vasya, "c7", "c6", -1, ["c6"], MoveReason.RunCorrection, runId), s.Last.Events[2]);
        Assert.Equal(new ProofApproved(runId, vasya, false, LowerByProof, s.Clock.UtcNow), s.Last.Events[3]);

        // And the run counts at normal
        var run = s.State.Runs[runId];
        Assert.Equal(Difficulty.Normal, run.Difficulty);
        Assert.Equal([new Die(4, 4), new Die(4, 2)], run.Dice);
        Assert.Equal(ProofStatus.Approved, run.Proof!.Status);
        Assert.Equal((6, "c6"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Lowering_from_extreme_by_the_proof_marks_the_pending_good_event_not_applicable()
    {
        // «Выше сложной» → hard: both d6, so no points change; the good event waits and is not applicable any more
        var (s, runId) = Completed([5, 2], Difficulty.Extreme);
        var vasya = s.PlayerId("Вася");
        var effectId = PendingEffectOf(s, runId);
        Submit(s, "Вася", runId, [Link]);

        Approve(s, runId, Difficulty.Hard, LowerByProof);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunDifficultyChanged), typeof(ManualEffectResolved), typeof(ProofApproved)],
            s.Last.Events.Select(e => e.GetType()));
        var resolved = Assert.IsType<ManualEffectResolved>(s.Last.Events[1]);
        Assert.Equal(
            (effectId, vasya, (Guid?)runId, ManualEffectOutcome.NotApplicable),
            (resolved.EffectId, resolved.PlayerId, resolved.RunId, resolved.Outcome));
        Assert.Equal(LowerByProof, resolved.Comment);
        Assert.Empty(s.State.ManualEffects);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Approve_at_a_lower_difficulty_without_a_comment_is_rejected(string? comment)
    {
        // D-98 (3): like «одобрить без скрина», a lower difficulty needs the admin's reason; the engine has no default text
        var (s, runId) = Completed([5, 2], Difficulty.Hard);
        Submit(s, "Вася", runId, [Link]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, runId, Difficulty.Normal, comment), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Lower_difficulty_without_a_proof_is_approved_the_same_way()
    {
        var (s, runId) = Completed([5, 2], Difficulty.Hard);

        Approve(s, runId, Difficulty.Normal, WithoutProof);

        ScenarioAssert.Accepted(s);
        Assert.Equal(typeof(RunDifficultyChanged), s.Last.Events[0].GetType());
        Assert.Equal(new ProofApproved(runId, s.PlayerId("Вася"), true, WithoutProof, s.Clock.UtcNow), s.Last.Events[^1]);
        Assert.Equal(Difficulty.Normal, s.State.Runs[runId].Difficulty);
    }

    [Fact]
    public void Same_difficulty_by_the_proof_just_approves()
    {
        var (s, runId) = Completed([5, 2], Difficulty.Hard);
        Submit(s, "Вася", runId, [Link]);
        var players = s.State.Players;

        Approve(s, runId, Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new ProofApproved(runId, s.PlayerId("Вася"), false, null, s.Clock.UtcNow)], s.Last.Events);
        Assert.Equal(Difficulty.Hard, s.State.Runs[runId].Difficulty);
        Assert.Equal(players, s.State.Players);
    }

    [Fact]
    public void Higher_difficulty_than_claimed_is_rejected()
    {
        // D-98: the proof does not raise the difficulty — the admin changes it separately (ChangeRunDifficulty)
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, runId, Difficulty.Hard), RejectionCodes.ProofDifficultyAboveClaimed);
    }

    [Fact]
    public void Undefined_difficulty_is_rejected_as_an_invalid_command()
    {
        var (s, runId) = Completed([5, 2], Difficulty.Hard);
        Submit(s, "Вася", runId, [Link]);

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, runId, (Difficulty)42), RejectionCodes.CommandInvalid);
    }

    // ---- Which runs ----

    [Fact]
    public void Approving_twice_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link]);
        Approve(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, runId), RejectionCodes.ProofAlreadyReviewed);
    }

    [Fact]
    public void Approving_a_rejected_run_is_rejected()
    {
        var (s, runId) = Completed([3, 1]);
        Reject(s, runId);
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, runId, comment: WithoutProof), RejectionCodes.ProofAlreadyReviewed);
    }

    [Fact]
    public void Run_being_played_is_rejected()
    {
        var (s, _) = Completed([3, 1]);
        s.Roll("Вася").Start("Вася");
        var playing = s.Player("Вася").ActiveRunId!.Value;

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, playing, comment: WithoutProof), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Dropped_run_is_rejected()
    {
        var (s, _) = Completed([3, 1]);
        var dropped = DroppedRun(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, dropped, comment: WithoutProof), RejectionCodes.RunNotCompleted);
    }

    [Fact]
    public void Unknown_run_is_rejected()
    {
        var (s, _) = Completed([3, 1]);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => Approve(x, SequentialIds.Make(0x0BAD0000, 1), comment: WithoutProof), RejectionCodes.RunUnknown);
    }

    // ---- The season ----

    [Fact]
    public void Approval_is_allowed_while_the_season_is_closing()
    {
        // SPEC: «закрытие (дедлайн прошёл, идёт проверка пруфов)»
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link]);
        MoveSeasonTo(s, SeasonStatus.Closing);

        Approve(s, runId);

        ScenarioAssert.Accepted(s);
        Assert.Equal(ProofStatus.Approved, s.State.Runs[runId].Proof!.Status);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void Approval_after_the_season_is_finished_is_rejected(SeasonStatus status)
    {
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link]);
        s.MoveStatusToForcingFinish(status); // the run stays unchecked on purpose

        ScenarioAssert.RejectsWithoutChanges(s, x => Approve(x, runId), RejectionCodes.SeasonClosed);
    }

    // ---- After the approval ----

    [Fact]
    public void Hours_can_still_be_corrected_after_the_approval()
    {
        // D-98: corrections (C7b) stay available to fix mistakes
        var (s, runId) = Completed([3, 1]);
        Submit(s, "Вася", runId, [Link]);
        Approve(s, runId);

        s.NextRandom(2, 2);
        s.Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(8, s.Player("Вася").Points);
        Assert.Equal(ProofStatus.Approved, s.State.Runs[runId].Proof!.Status);
    }

    [Fact]
    public void Difficulty_can_still_be_changed_after_the_approval()
    {
        var (s, runId) = Completed([3, 1]);
        Approve(s, runId, comment: WithoutProof);

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "Пересмотрели пруф"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Difficulty.Hard, s.State.Runs[runId].Difficulty);
        Assert.Equal(ProofStatus.Approved, s.State.Runs[runId].Proof!.Status);
    }

    [Fact]
    public void Approval_touches_nobody_else()
    {
        var (s, runId) = Completed([3, 1]);
        CompleteRun(s, "Петя", [2, 2]);
        var petya = s.Player("Петя");
        var otherRuns = s.State.Runs.Where(r => r.Key != runId).ToList();

        Approve(s, runId, comment: WithoutProof);

        ScenarioAssert.Accepted(s);
        Assert.Equal(petya, s.Player("Петя"));
        Assert.Equal(otherRuns, s.State.Runs.Where(r => r.Key != runId).ToList());
    }

    [Fact]
    public void Approval_does_not_touch_the_owners_turn()
    {
        var (s, runId) = Completed([3, 1]);
        s.Roll("Вася").Start("Вася");
        var vasya = s.Player("Вася");

        Approve(s, runId, comment: WithoutProof);

        ScenarioAssert.Accepted(s);
        Assert.Equal(vasya, s.Player("Вася"));
    }

    // ---- The log ----

    [Fact]
    public void Replaying_the_log_gives_the_same_state_after_approvals()
    {
        var (s, runId) = Completed([5, 2], Difficulty.Extreme);
        var second = CompleteRun(s, "Вася", [2, 2]);
        Submit(s, "Вася", runId, [Link]);
        Approve(s, runId, Difficulty.Normal, LowerByProof);
        ScenarioAssert.Accepted(s);
        Approve(s, second, comment: WithoutProof);
        ScenarioAssert.Accepted(s);

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void Folding_the_approval_events_alone_updates_the_run_the_player_and_the_effects()
    {
        var (s, runId) = Completed([5, 2], Difficulty.Extreme);
        Submit(s, "Вася", runId, [Link]);
        var before = s.State;

        Approve(s, runId, Difficulty.Normal, LowerByProof);

        ScenarioAssert.Accepted(s);
        Assert.Equal(s.State, s.Last.Events.Aggregate(before, SeasonEngine.Apply));
    }
}
