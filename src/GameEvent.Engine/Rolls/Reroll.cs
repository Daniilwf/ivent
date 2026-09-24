using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rolls;

/// <summary>
/// «Реролл» before starting (SPEC «Реролл, дроп, тех-реролл»): the offered game (or the whole pending choice) is given
/// up and the wheel spins again. Payment in order (D-07): a free reroll of this roll (<c>roll.freeRerollsPerRoll</c>),
/// then the <c>freeRerolls</c> resource, then <c>roll.rerollCost</c> — coins or a bad event (Q-2).
/// </summary>
public sealed record Reroll(Guid PlayerId) : ICommand;

/// <summary>How a reroll was paid for.</summary>
public enum RerollPayment
{
    /// <summary>One of the free rerolls every roll gives.</summary>
    FreeThisRoll,

    /// <summary>One <c>freeRerolls</c> resource (a reroll coupon).</summary>
    FreeRerollResource,

    /// <summary><c>roll.rerollCost.amount</c> coins.</summary>
    Coins,

    /// <summary>A manual «draw a bad event» effect.</summary>
    BadEvent,
}

/// <summary>
/// The player gave up <see cref="GameIds"/> (the offer, or every option of the pending choice). The same command then
/// writes the payment (<c>ResourceChanged</c>, <c>CoinsChanged</c> or <c>ManualEffectCreated</c>) and the new roll,
/// which never offers a game just given up.
/// </summary>
[EventType("game-rerolled")]
public sealed record GameRerolled(Guid PlayerId, EquatableArray<Guid> GameIds, RerollPayment Payment) : IGameEvent;
