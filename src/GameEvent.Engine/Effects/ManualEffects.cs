using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Effects;

/// <summary>What created a manual effect; C11 adds item, cell and event sources.</summary>
public enum ManualEffectSource
{
    /// <summary>A paid reroll with <c>roll.rerollCost.kind = badEvent</c>.</summary>
    PaidReroll,

    /// <summary>The mandatory bad event of a drop (<c>drop.mandatoryEvent = bad</c>), also after a converted tech reroll.</summary>
    Drop,

    /// <summary>The event a difficulty grants on completion (<c>dieByDifficulty.*.grantEvent</c>: «выше сложной» — good).</summary>
    Difficulty,
}

/// <summary>
/// A text effect waiting to be resolved by the player or the admin (GLOSSARY «Ручной эффект»). Stage 1 knows one kind:
/// «Разыграй плохой/хороший ивент» (D-10, the <c>drawEvent</c> action with events off). Resolution comes with C11.
/// </summary>
public sealed record PendingManualEffect(Guid EffectId, Guid PlayerId, EventKind DrawEvent, ManualEffectSource Source, Guid? RunId);

/// <summary>How a manual effect was resolved (GLOSSARY «Ручной эффект»: применено / не применимо + комментарий).</summary>
public enum ManualEffectOutcome
{
    Applied,
    NotApplicable,
}

/// <summary>
/// Resolve a pending manual effect (D-31, D-102): the owner (<see cref="PlayerId"/> set by the player endpoint) or the
/// admin (<see cref="PlayerId"/> null, set by the admin endpoint) marks it applied or not applicable. «Не применимо» needs a
/// comment; the admin always writes one (D-89); the player's «применено» may go without.
/// </summary>
public sealed record ResolveManualEffect(Guid EffectId, ManualEffectOutcome Outcome, string? Comment, Guid? PlayerId) : ICommand;

/// <summary>
/// A manual effect is resolved and no longer pending: by the owner or the admin (<see cref="ResolveManualEffect"/>), or
/// by the engine when a run's difficulty drops below the one that granted it (Q-5). <see cref="Comment"/> is empty when the
/// owner applied it without one.
/// </summary>
[EventType("manual-effect-resolved")]
public sealed record ManualEffectResolved(Guid EffectId, Guid PlayerId, Guid? RunId, ManualEffectOutcome Outcome, string Comment) : IGameEvent;

[EventType("manual-effect-created")]
public sealed record ManualEffectCreated(Guid EffectId, Guid PlayerId, EventKind DrawEvent, ManualEffectSource Source, Guid? RunId) : IGameEvent;

internal static class ManualEffects
{
    public static Decision Decide(Seasons.SeasonState state, ResolveManualEffect command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        // Pending effects are settled while the season runs or closes; after the finish the log is final (D-101).
        if (state.Status is not (Seasons.SeasonStatus.Active or Seasons.SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}.");
        }

        if (!Enum.IsDefined(command.Outcome))
        {
            return Decision.Reject(RejectionCodes.EffectUnknownOutcome, $"Unknown outcome {command.Outcome}.");
        }

        if (!state.ManualEffects.TryGetValue(command.EffectId, out var effect))
        {
            return Decision.Reject(RejectionCodes.EffectNotPending, $"Manual effect {command.EffectId} is not pending.");
        }

        if (command.PlayerId is { } playerId && playerId != effect.PlayerId)
        {
            return Decision.Reject(RejectionCodes.EffectNotYours, "A player resolves only their own manual effects.");
        }

        var comment = command.Comment?.Trim() ?? "";
        var needsComment = command.PlayerId is null || command.Outcome == ManualEffectOutcome.NotApplicable;
        if (needsComment && comment.Length == 0)
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "«Не применимо» and every admin resolution explain themselves.");
        }

        return comment.Length > Limits.MaxCommentLength
            ? Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.")
            : Decision.Accept(new ManualEffectResolved(effect.EffectId, effect.PlayerId, effect.RunId, command.Outcome, comment));
    }

    public static Seasons.SeasonState Apply(Seasons.SeasonState state, ManualEffectResolved e) =>
        state.ManualEffects.ContainsKey(e.EffectId)
            ? state with { ManualEffects = state.ManualEffects.Remove(e.EffectId) }
            : throw new InvalidOperationException($"Manual effect {e.EffectId} is not pending.");

    public static Seasons.SeasonState Apply(Seasons.SeasonState state, ManualEffectCreated e) =>
        state with
        {
            ManualEffects = state.ManualEffects.Add(e.EffectId, new PendingManualEffect(e.EffectId, e.PlayerId, e.DrawEvent, e.Source, e.RunId)),
        };
}
