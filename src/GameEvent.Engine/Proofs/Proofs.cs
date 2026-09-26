using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Proofs;

/// <summary>Where a run's proof stands (SPEC «Очередь пруфов»).</summary>
public enum ProofStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// The proof of a completed run (D-98): links to screenshots or videos (http/https), uploaded screenshots
/// (<see cref="Files"/>, D-116), a note, or another player of the season who saw the run. <see cref="SubmittedAt"/> is
/// null when the admin approved without a proof («без скрина»).
/// </summary>
public sealed record ProofState(
    ProofStatus Status,
    EquatableArray<string> Links,
    string? Note,
    Guid? WitnessId,
    DateTimeOffset? SubmittedAt,
    string? Comment,

    // Not written while empty: undo snapshots of proofs without screenshots keep their frozen format (D-116)
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    EquatableArray<Guid> Files = default);

/// <summary>
/// The player sends the proof of their completed run; while unchecked, a new one replaces the old (D-98).
/// <see cref="Files"/> — screenshots the player uploaded (D-116): the site checks they are his before the command.
/// </summary>
public sealed record SubmitProof(
    Guid PlayerId, Guid RunId, EquatableArray<string> Links, string? Note = null, Guid? WitnessId = null, EquatableArray<Guid> Files = default) : ICommand;

/// <summary>
/// The admin approves a run: with its proof, or without one («одобрить без скрина», a comment then). A
/// <see cref="Difficulty"/> below the claimed one counts the run at that difficulty (SPEC «Сложность засчитывается по
/// пруфу», Q-5).
/// </summary>
public sealed record ApproveProof(Guid RunId, Difficulty? Difficulty = null, string? Comment = null) : ICommand;

/// <summary>The admin rejects a run (D-15): its points, cells and completion coins are taken back.</summary>
public sealed record RejectProof(Guid RunId, string Comment) : ICommand;

/// <summary>The player's proof, as checked. v2 (D-116) adds <see cref="Files"/>; a v1 record reads with none.</summary>
[EventType("proof-submitted", version: 2)]
public sealed record ProofSubmitted(
    Guid RunId, Guid PlayerId, EquatableArray<string> Links, string? Note, Guid? WitnessId, DateTimeOffset SubmittedAt, EquatableArray<Guid> Files) : IGameEvent;

[EventType("proof-approved")]
public sealed record ProofApproved(Guid RunId, Guid PlayerId, bool WithoutProof, string? Comment, DateTimeOffset ApprovedAt) : IGameEvent;

/// <summary>The run is rejected; the events taking back its points, cells and coins follow in the same command.</summary>
[EventType("proof-rejected")]
public sealed record ProofRejected(Guid RunId, Guid PlayerId, string Comment, DateTimeOffset RejectedAt) : IGameEvent;

/// <summary>The admin's queue of runs to check (SPEC «Уточнения», Q-3: the runs that decide a finish go on top).</summary>
public static class ProofReviewOrder
{
    /// <summary>A standing finisher's run up to the finish: it decides the places and goes on top of the queue (Q-3).</summary>
    public static bool DecidesFinish(SeasonState state, RunState run)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(run);
        return state.Players[run.PlayerId].Finish is not null && !run.AfterFinish;
    }

    /// <summary>
    /// Completed runs not yet approved or rejected: the runs of standing finishers up to their finish first (they decide the
    /// places, Q-3), then the earliest completed; ties by run id.
    /// </summary>
    public static IReadOnlyList<Guid> Order(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // A finisher's runs up to the finish decide the first place (Q-3): they go on top, with the run that reached it.
        return [.. state.Runs.Values
            .Where(Rolls.UncheckedRuns.Waits)
            .OrderByDescending(r => DecidesFinish(state, r))
            .ThenBy(r => r.CompletedAt)
            .ThenBy(r => r.RunId)
            .Select(r => r.RunId)];
    }
}

internal static class ProofReview
{
    public static Decision Decide(SeasonState state, SubmitProof command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (SeasonGuard(state) is { } closed)
        {
            return closed;
        }

        if (!state.Players.ContainsKey(command.PlayerId))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        if (!state.Runs.TryGetValue(command.RunId, out var run))
        {
            return Decision.Reject(RejectionCodes.RunUnknown, $"Run {command.RunId} is not in the season.");
        }

        if (run.PlayerId != command.PlayerId)
        {
            return Decision.Reject(RejectionCodes.NotYourRun, "Only the player who completed a run proves it.");
        }

        if (Reviewed(run) is { } reviewed)
        {
            return reviewed;
        }

        if (run.Status != RunStatus.Completed)
        {
            return Decision.Reject(RejectionCodes.RunNotCompleted, $"Run {run.RunId} is {run.Status}.");
        }

        if (command.Links.Count > Limits.MaxProofLinks || command.Links.Any(link => !ProofLinks.IsValid(link)))
        {
            return Decision.Reject(
                RejectionCodes.ProofInvalidLink,
                $"Up to {Limits.MaxProofLinks} http or https links of at most {Limits.MaxProofLinkLength} characters.");
        }

        if (command.Files.Count > Limits.MaxProofFiles || command.Files.Distinct().Count() != command.Files.Count || command.Files.Contains(Guid.Empty))
        {
            return Decision.Reject(RejectionCodes.ProofInvalidFile, $"Up to {Limits.MaxProofFiles} different screenshots.");
        }

        if (command.Links.Count == 0 && command.Files.Count == 0 && command.WitnessId is null)
        {
            return Decision.Reject(RejectionCodes.ProofEmpty, "A proof is a link, a screenshot or a witness.");
        }

        if (command.WitnessId is { } witness && (witness == command.PlayerId || !state.Players.ContainsKey(witness)))
        {
            return Decision.Reject(RejectionCodes.ProofWitnessInvalid, "A witness is another player of the season.");
        }

        if (command.Note?.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The note is limited to {Limits.MaxCommentLength} characters.");
        }

        // Stored as checked: links and the note trimmed, a blank note is none.
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        return Decision.Accept(new ProofSubmitted(
            run.RunId, run.PlayerId, [.. command.Links.Select(l => l.Trim())], note, command.WitnessId, context.Clock.UtcNow, command.Files));
    }

    public static Decision Decide(SeasonState state, ApproveProof command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (AdminGuard(state, command.RunId) is { } rejection)
        {
            return rejection;
        }

        var run = state.Runs[command.RunId];
        var claimed = run.Difficulty ?? throw new InvalidOperationException($"Completed run {run.RunId} has no difficulty.");
        if (command.Difficulty is { } proven)
        {
            if (!Enum.IsDefined(proven))
            {
                return Decision.Reject(RejectionCodes.CommandInvalid, $"Unknown difficulty {proven}.");
            }

            if (proven > claimed)
            {
                return Decision.Reject(RejectionCodes.ProofDifficultyAboveClaimed, "A proof does not raise the claimed difficulty.");
            }
        }

        // Approving without a proof, or at a lower difficulty than claimed, explains itself in the public log.
        var withoutProof = run.Proof is null;
        var lowered = command.Difficulty is { } asked && asked < claimed;
        if ((withoutProof || lowered) && string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Approving without a proof or at a lower difficulty needs a comment.");
        }

        if (command.Comment?.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        // SPEC «Сложность засчитывается по пруфу»: a lower proven difficulty recalculates the dice first (Q-5, D-98).
        var events = new List<IGameEvent>();
        if (command.Difficulty is { } lower && lower < claimed)
        {
            events.AddRange(Corrections.DifficultyChange(state, run, lower, command.Comment!, context));
        }

        events.Add(new ProofApproved(run.RunId, run.PlayerId, withoutProof, command.Comment, context.Clock.UtcNow));

        // The first place becomes final once every run up to the finish is approved (Q-3).
        events.AddRange(Finishes.FreezeIfDue(events.Aggregate(state, SeasonEngine.Apply)));
        return Decision.Accept(events);
    }

    public static Decision Decide(SeasonState state, RejectProof command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (AdminGuard(state, command.RunId) is { } rejection)
        {
            return rejection;
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "A reject explains itself in the public log.");
        }

        if (command.Comment.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        // D-15, D-98: what this run gave is taken back — its dice (after every correction), the cells it moved the
        // token, its completion coins; its pending difficulty event is not applicable.
        var run = state.Runs[command.RunId];
        var player = state.Players[run.PlayerId];
        var events = new List<IGameEvent> { new ProofRejected(run.RunId, run.PlayerId, command.Comment, context.Clock.UtcNow) };

        // The frozen first loses nothing (Q-3); his runs up to the finish are approved, so only later ones get here.
        // The rejected run's pending difficulty event still does not apply (D-98, D-99).
        if (Finishes.IsFrozen(player))
        {
            events.AddRange(PendingDifficultyEvents(state, run, command.Comment));
            return Decision.Accept(events);
        }

        var points = run.Dice.Sum(d => d.Value) + run.ChallengeDice.Sum(d => d.Value);
        if (points != 0)
        {
            events.Add(new PointsChanged(run.PlayerId, -points, PointsReason.ProofRejected, run.RunId));
        }

        // The cells this run really moved the token (steps that burned at the finish gave none); a finisher's position
        // changes only through a run up to the finish, and then by the surplus rule (Q-3).
        if (player.Finish is null && run.Moved != 0)
        {
            var path = run.Moved > 0
                ? Movement.Backward(state.Map, player.Path, run.Moved)
                : Movement.Forward(state.Map, player.CellId, -run.Moved);
            if (path.Count > 0)
            {
                events.Add(new PlayerMoved(
                    run.PlayerId, player.CellId, path[^1], -run.Moved, [.. path], MoveReason.ProofRejected, run.RunId));
            }
        }

        var coins = run.Snapshot.Coins is { } reward && run.Hours is { } hours
            ? RunLifecycle.CompletionCoins(reward, run.Snapshot.DiceCount, hours)
            : 0;
        if (coins != 0)
        {
            events.Add(new CoinsChanged(run.PlayerId, -coins, CoinsReason.ProofRejected, run.RunId));
        }

        events.AddRange(PendingDifficultyEvents(state, run, command.Comment));

        if (Finishes.CountsForFinish(player, run))
        {
            var after = events.Aggregate(state, SeasonEngine.Apply);
            var taken = player.Finish!.RunId == run.RunId ? points : run.Moved;
            events.AddRange(Finishes.AfterReduction(after, after.Players[player.PlayerId], after.Runs[run.RunId], taken, MoveReason.ProofRejected));
        }

        return Decision.Accept(events);
    }

    private static IEnumerable<IGameEvent> PendingDifficultyEvents(SeasonState state, RunState run, string comment) =>
        state.ManualEffects.Values
            .Where(e => e.RunId == run.RunId && e.Source == ManualEffectSource.Difficulty)
            .Select(e => new ManualEffectResolved(e.EffectId, e.PlayerId, e.RunId, ManualEffectOutcome.NotApplicable, comment));

    public static SeasonState Apply(SeasonState state, ProofSubmitted e) =>
        Update(state, e.RunId, run => run with
        {
            Proof = new ProofState(ProofStatus.Pending, e.Links, e.Note, e.WitnessId, e.SubmittedAt, Comment: null, e.Files),
        });

    public static SeasonState Apply(SeasonState state, ProofApproved e) =>
        Update(state, e.RunId, run => run with
        {
            Proof = run.Proof is { } proof
                ? proof with { Status = ProofStatus.Approved, Comment = e.Comment }
                : new ProofState(ProofStatus.Approved, [], Note: null, WitnessId: null, SubmittedAt: null, e.Comment),
        });

    public static SeasonState Apply(SeasonState state, ProofRejected e) =>
        Update(state, e.RunId, run => run with
        {
            Status = RunStatus.Rejected,
            Proof = run.Proof is { } proof
                ? proof with { Status = ProofStatus.Rejected, Comment = e.Comment }
                : new ProofState(ProofStatus.Rejected, [], Note: null, WitnessId: null, SubmittedAt: null, e.Comment),
        });

    private static SeasonState Update(SeasonState state, Guid runId, Func<RunState, RunState> change) =>
        state with { Runs = state.Runs.SetItem(runId, change(state.Runs[runId])) };

    // Proofs are sent and checked while the season runs or closes (SPEC: proofs are accepted after the deadline).
    private static Decision? SeasonGuard(SeasonState state) =>
        !state.IsCreated
            ? Decision.Reject(RejectionCodes.SeasonNotCreated, "The season does not exist yet.")
            : state.Status is not (SeasonStatus.Active or SeasonStatus.Closing)
                ? Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.")
                : null;

    private static Decision? AdminGuard(SeasonState state, Guid runId)
    {
        if (SeasonGuard(state) is { } closed)
        {
            return closed;
        }

        if (!state.Runs.TryGetValue(runId, out var run))
        {
            return Decision.Reject(RejectionCodes.RunUnknown, $"Run {runId} is not in the season.");
        }

        return Reviewed(run)
            ?? (run.Status == RunStatus.Completed
                ? null
                : Decision.Reject(RejectionCodes.RunNotCompleted, $"Run {run.RunId} is {run.Status}."));
    }

    // A run is checked once: approved or rejected (D-98).
    private static Decision? Reviewed(RunState run) =>
        run.Status == RunStatus.Rejected || run.Proof?.Status is ProofStatus.Approved or ProofStatus.Rejected
            ? Decision.Reject(RejectionCodes.ProofAlreadyReviewed, $"Run {run.RunId} is already checked.")
            : null;

}

/// <summary>What a proof link may be: an absolute http or https address with a host, not too long (D-98).</summary>
public static class ProofLinks
{
    public static bool IsValid(string? link) =>
        !string.IsNullOrWhiteSpace(link)
        && link.Length <= Limits.MaxProofLinkLength
        && Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);
}
