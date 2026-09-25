using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
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
/// The proof of a completed run (D-98): links to screenshots or videos (http/https), a note, or another player of the
/// season who saw the run. <see cref="SubmittedAt"/> is null when the admin approved without a proof («без скрина»).
/// </summary>
public sealed record ProofState(
    ProofStatus Status,
    EquatableArray<string> Links,
    string? Note,
    Guid? WitnessId,
    DateTimeOffset? SubmittedAt,
    string? Comment);

/// <summary>The player sends the proof of their completed run; while unchecked, a new one replaces the old (D-98).</summary>
public sealed record SubmitProof(Guid PlayerId, Guid RunId, EquatableArray<string> Links, string? Note = null, Guid? WitnessId = null) : ICommand;

/// <summary>
/// The admin approves a run: with its proof, or without one («одобрить без скрина», a comment then). A
/// <see cref="Difficulty"/> below the claimed one counts the run at that difficulty (SPEC «Сложность засчитывается по
/// пруфу», Q-5).
/// </summary>
public sealed record ApproveProof(Guid RunId, Difficulty? Difficulty = null, string? Comment = null) : ICommand;

/// <summary>The admin rejects a run (D-15): its points, cells and completion coins are taken back.</summary>
public sealed record RejectProof(Guid RunId, string Comment) : ICommand;

[EventType("proof-submitted")]
public sealed record ProofSubmitted(
    Guid RunId, Guid PlayerId, EquatableArray<string> Links, string? Note, Guid? WitnessId, DateTimeOffset SubmittedAt) : IGameEvent;

[EventType("proof-approved")]
public sealed record ProofApproved(Guid RunId, Guid PlayerId, bool WithoutProof, string? Comment, DateTimeOffset ApprovedAt) : IGameEvent;

/// <summary>The run is rejected; the events taking back its points, cells and coins follow in the same command.</summary>
[EventType("proof-rejected")]
public sealed record ProofRejected(Guid RunId, Guid PlayerId, string Comment, DateTimeOffset RejectedAt) : IGameEvent;

/// <summary>The admin's queue of runs to check (SPEC «Уточнения»: a run that reached the finish goes on top).</summary>
public static class ProofReviewOrder
{
    /// <summary>
    /// Completed runs not yet approved or rejected: those that brought their player to the finish first, then the
    /// earliest completed; ties by run id.
    /// </summary>
    public static IReadOnlyList<Guid> Order(SeasonState state) =>
        throw new NotImplementedException("C8");
}

internal static class ProofReview
{
    public static Decision Decide(SeasonState state, SubmitProof command, EngineContext context) =>
        throw new NotImplementedException("C8");

    public static Decision Decide(SeasonState state, ApproveProof command, EngineContext context) =>
        throw new NotImplementedException("C8");

    public static Decision Decide(SeasonState state, RejectProof command, EngineContext context) =>
        throw new NotImplementedException("C8");

    public static SeasonState Apply(SeasonState state, ProofSubmitted e) =>
        throw new NotImplementedException("C8");

    public static SeasonState Apply(SeasonState state, ProofApproved e) =>
        throw new NotImplementedException("C8");

    public static SeasonState Apply(SeasonState state, ProofRejected e) =>
        throw new NotImplementedException("C8");
}
