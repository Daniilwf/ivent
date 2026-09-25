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
/// A manual effect is resolved and no longer pending. Stage 1 writes it when a run's difficulty drops below the one that
/// granted the effect (Q-5); the player's and the admin's own commands come with C11.
/// </summary>
[EventType("manual-effect-resolved")]
public sealed record ManualEffectResolved(Guid EffectId, Guid PlayerId, Guid? RunId, ManualEffectOutcome Outcome, string Comment) : IGameEvent;

[EventType("manual-effect-created")]
public sealed record ManualEffectCreated(Guid EffectId, Guid PlayerId, EventKind DrawEvent, ManualEffectSource Source, Guid? RunId) : IGameEvent;

internal static class ManualEffects
{
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
