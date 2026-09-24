using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Effects;

/// <summary>What created a manual effect; C11 adds item, cell and event sources.</summary>
public enum ManualEffectSource
{
    /// <summary>A paid reroll with <c>roll.rerollCost.kind = badEvent</c>.</summary>
    PaidReroll,
}

/// <summary>
/// A text effect waiting to be resolved by the player or the admin (GLOSSARY «Ручной эффект»). Stage 1 knows one kind:
/// «Разыграй плохой/хороший ивент» (D-10, the <c>drawEvent</c> action with events off). Resolution comes with C11.
/// </summary>
public sealed record PendingManualEffect(Guid EffectId, Guid PlayerId, EventKind DrawEvent, ManualEffectSource Source, Guid? RunId);

[EventType("manual-effect-created")]
public sealed record ManualEffectCreated(Guid EffectId, Guid PlayerId, EventKind DrawEvent, ManualEffectSource Source, Guid? RunId) : IGameEvent;

internal static class ManualEffects
{
    public static Seasons.SeasonState Apply(Seasons.SeasonState state, ManualEffectCreated e) =>
        throw new NotImplementedException("C6");
}
