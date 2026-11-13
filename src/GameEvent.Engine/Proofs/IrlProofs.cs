using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Proofs;

/// <summary>Where the proof of an IRL event stands (D-509).</summary>
public enum IrlProofStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// The proof of an IRL effect (D-509): the player's own uploaded screenshots (<see cref="Files"/>, checked as his by the site
/// before the command, D-116) and / or one https <see cref="Link"/> to a video, which the server never opens or downloads.
/// <see cref="Comment"/> — the admin's, after a reject.
/// </summary>
public sealed record IrlProofState(IrlProofStatus Status, EquatableArray<Guid> Files, string? Link, DateTimeOffset SubmittedAt, string? Comment);

/// <summary>
/// The player sends the proof of his IRL effect (D-509): up to <c>events.irlProof.maxFiles</c> screenshots and / or one https
/// link. One submission waits at a time; after a reject he sends a new one.
/// </summary>
public sealed record SubmitIrlProof(Guid PlayerId, Guid EffectId, EquatableArray<Guid> Files, string? Link = null) : ICommand;

/// <summary>
/// The admin checks the waiting IRL proof (D-509): approval resolves the manual effect «применено» and gives nothing; a reject
/// explains itself (a comment) and the effect waits for a new proof, without a penalty.
/// </summary>
public sealed record ReviewIrlProof(Guid EffectId, bool Approved, string? Comment = null) : ICommand;

/// <summary>The proof of an IRL effect, as checked: the link trimmed, null when there is none.</summary>
[EventType("irl-proof-submitted")]
public sealed record IrlProofSubmitted(Guid EffectId, Guid PlayerId, EquatableArray<Guid> Files, string? Link, DateTimeOffset SubmittedAt) : IGameEvent;

/// <summary>
/// The admin's review of an IRL proof. An approval is followed in the same command by the effect's
/// <see cref="ManualEffectResolved"/> «применено»; a reject leaves the effect waiting.
/// </summary>
[EventType("irl-proof-reviewed")]
public sealed record IrlProofReviewed(Guid EffectId, Guid PlayerId, bool Approved, string? Comment, DateTimeOffset ReviewedAt) : IGameEvent;

/// <summary>IRL proofs (SPEC «IRL-ивенты», D-509): a manual effect of an object with <c>proof: media</c>.</summary>
public static class IrlProofs
{
    /// <summary>
    /// Whether the effect is an IRL one: its object's definition in the season's catalog (deleted ones too) asks for a media
    /// proof (D-509: the mark comes from the definition, <c>manual-effect-created</c> does not change).
    /// </summary>
    public static bool IsIrl(SeasonState state, PendingManualEffect effect)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(effect);
        return effect.ObjectId is { } id && state.Catalog.Find(id)?.Proof == ContentProof.Media;
    }

    /// <summary>The effects whose submitted proof waits for the admin: the season's results wait for them (D-516, D-546 (1)).</summary>
    public static IReadOnlyList<Guid> Waiting(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return [.. state.ManualEffects.Values.Where(e => e.IrlProof?.Status == IrlProofStatus.Pending).Select(e => e.EffectId)];
    }

    /// <summary>
    /// What an IRL proof link may be: one absolute https address with a host and no user info, not too long, without control
    /// or format characters (bidi marks the parser would drop, so the shown address would differ from the checked one) and
    /// without backslashes (browsers read them as slashes) — D-680.
    /// </summary>
    public static bool IsValidLink(string link) =>
        link.Length <= Limits.MaxProofLinkLength
        && !link.Any(c => c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c)
            || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
        && Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host)
        && string.IsNullOrEmpty(uri.UserInfo);

    internal static Decision Decide(SeasonState state, SubmitIrlProof command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (Guard(state) is { } refused)
        {
            return refused;
        }

        if (!state.Players.ContainsKey(command.PlayerId))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        if (!state.ManualEffects.TryGetValue(command.EffectId, out var effect))
        {
            return Decision.Reject(RejectionCodes.EffectNotPending, $"Manual effect {command.EffectId} is not pending.");
        }

        if (effect.PlayerId != command.PlayerId)
        {
            return Decision.Reject(RejectionCodes.EffectNotYours, "A player proves only his own IRL effects.");
        }

        if (!IsIrl(state, effect))
        {
            return Decision.Reject(RejectionCodes.IrlProofNotIrl, "Only an IRL event takes a proof.");
        }

        if (effect.IrlProof?.Status == IrlProofStatus.Pending)
        {
            return Decision.Reject(RejectionCodes.IrlProofPending, "The proof waits for the admin: a new one goes after a reject.");
        }

        var maxFiles = state.Rules.Events?.IrlProof?.MaxFiles ?? 0;
        var files = command.Files;
        if (files.Count > maxFiles || files.Distinct().Count() != files.Count || files.Contains(Guid.Empty))
        {
            return Decision.Reject(RejectionCodes.IrlProofInvalidFile, $"Up to {maxFiles} different screenshots.");
        }

        // A blank link is none; a link is never opened by the server, only shown (D-509)
        var link = string.IsNullOrWhiteSpace(command.Link) ? null : command.Link.Trim();
        if (link is not null && !IsValidLink(link))
        {
            return Decision.Reject(
                RejectionCodes.IrlProofInvalidLink, $"One https link with a host and no user info, of at most {Limits.MaxProofLinkLength} characters.");
        }

        return link is null && files.Count == 0
            ? Decision.Reject(RejectionCodes.IrlProofEmpty, "An IRL proof is a screenshot or a link to a video.")
            : Decision.Accept(new IrlProofSubmitted(effect.EffectId, effect.PlayerId, files, link, context.Clock.UtcNow));
    }

    internal static Decision Decide(SeasonState state, ReviewIrlProof command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (Guard(state) is { } refused)
        {
            return refused;
        }

        if (!state.ManualEffects.TryGetValue(command.EffectId, out var effect))
        {
            return Decision.Reject(RejectionCodes.EffectNotPending, $"Manual effect {command.EffectId} is not pending.");
        }

        if (effect.IrlProof?.Status != IrlProofStatus.Pending)
        {
            return Decision.Reject(RejectionCodes.IrlProofNotSubmitted, "No IRL proof of this effect waits for a review.");
        }

        var comment = string.IsNullOrWhiteSpace(command.Comment) ? null : command.Comment.Trim();
        if (!command.Approved && comment is null)
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "A reject explains itself to the player.");
        }

        if (comment?.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        var reviewed = new IrlProofReviewed(effect.EffectId, effect.PlayerId, command.Approved, comment, context.Clock.UtcNow);

        // D-509: approval resolves the effect «применено» and gives nothing — no points, coins or moves. The admin's comment
        // stays in the review, which only the player and the admin see (D-684): the public resolution carries none
        return command.Approved
            ? Decision.Accept(reviewed, new ManualEffectResolved(effect.EffectId, effect.PlayerId, effect.RunId, ManualEffectOutcome.Applied, ""))
            : Decision.Accept(reviewed);
    }

    // Proofs are sent and checked while the season runs or closes (D-516: after the deadline too); IRL events are events
    private static Decision? Guard(SeasonState state) =>
        !state.IsCreated
            ? Decision.Reject(RejectionCodes.SeasonNotCreated, "The season does not exist yet.")
            : state.Status is not (SeasonStatus.Active or SeasonStatus.Closing)
                ? Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.")
                : !state.Rules.Features.Events
                    ? Decision.Reject(RejectionCodes.FeatureDisabled, "Events are off in this season.")
                    : null;

    internal static SeasonState Apply(SeasonState state, IrlProofSubmitted e) =>
        Update(state, e.EffectId, effect => effect with
        {
            IrlProof = new IrlProofState(IrlProofStatus.Pending, e.Files, e.Link, e.SubmittedAt, Comment: null),
        });

    internal static SeasonState Apply(SeasonState state, IrlProofReviewed e) =>
        Update(state, e.EffectId, effect => effect with
        {
            IrlProof = effect.IrlProof! with { Status = e.Approved ? IrlProofStatus.Approved : IrlProofStatus.Rejected, Comment = e.Comment },
        });

    private static SeasonState Update(SeasonState state, Guid effectId, Func<PendingManualEffect, PendingManualEffect> change) =>
        state.ManualEffects.TryGetValue(effectId, out var effect)
            ? state with { ManualEffects = state.ManualEffects.SetItem(effectId, change(effect)) }
            : throw new InvalidOperationException($"Manual effect {effectId} is not pending.");
}
