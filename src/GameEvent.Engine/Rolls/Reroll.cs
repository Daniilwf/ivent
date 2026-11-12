using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rolls;

/// <summary>
/// «Реролл» before starting (SPEC «Реролл, дроп, тех-реролл»): the offered game (or the whole pending choice) is given
/// up and the wheel spins again. Payment in order (D-07): a free reroll of this roll (<c>roll.freeRerollsPerRoll</c>),
/// then the <c>freeRerolls</c> resource, then <c>roll.rerollCost</c> — coins or a bad event (Q-2).
/// </summary>
/// <remarks>
/// <see cref="ExpectedPayment"/> — the price the player saw (D-600): when set and the reroll would now be paid otherwise
/// (an older tab showed the free one), the command is refused with <c>turn.stale</c> and nothing is spent.
/// </remarks>
public sealed record Reroll(Guid PlayerId, RerollPayment? ExpectedPayment = null) : ICommand;

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

    /// <summary>The frozen first's reroll in free mode: nothing is spent (D-99).</summary>
    FreeMode,

    /// <summary>
    /// The offered game was deleted from the pool: nothing is spent and the free rerolls of the roll stay (D-615).
    /// </summary>
    GameDeleted,
}

/// <summary>
/// The player gave up <see cref="GameIds"/> (the offer, or every option of the pending choice). The same command then
/// writes the payment (<c>ResourceChanged</c>, <c>CoinsChanged</c> or <c>ManualEffectCreated</c>) and the new roll,
/// which never offers a game just given up.
/// </summary>
[EventType("game-rerolled")]
public sealed record GameRerolled(Guid PlayerId, EquatableArray<Guid> GameIds, RerollPayment Payment) : IGameEvent;

/// <summary>The price of the next reroll, as the command will charge it and the screen shows it (D-93).</summary>
public static class RerollPrice
{
    /// <summary>The reroll coupon resource (CONTENT.md «Купон реролла»).</summary>
    public const string FreeRerollsResource = "freeRerolls";

    /// <summary>
    /// The price of the next reroll for <paramref name="player"/>: nothing for an offered game the admin deleted (D-615),
    /// free in the first's free mode (D-99).
    /// </summary>
    public static (RerollPayment Payment, int Coins) For(Seasons.SeasonPlayer player, Rulesets.RollRules rules, bool offerDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(player);
        return Of(offerDeleted, player.Finish?.Frozen == true, player.RerollsThisRoll, player.Resources[FreeRerollsResource], rules);
    }

    /// <summary>
    /// The one order of the reroll price, shared by the command and the screen (D-600): a deleted offered game first
    /// (D-615), then the frozen first's free mode (D-99), then <see cref="Next"/>.
    /// </summary>
    public static (RerollPayment Payment, int Coins) Of(bool offerDeleted, bool frozen, int rerollsThisRoll, int coupons, Rulesets.RollRules rules) =>
        offerDeleted ? (RerollPayment.GameDeleted, 0)
        : frozen ? (RerollPayment.FreeMode, 0)
        : Next(rerollsThisRoll, coupons, rules);

    /// <summary>
    /// How the next reroll is paid after <paramref name="rerollsThisRoll"/> rerolls of this roll with
    /// <paramref name="coupons"/> reroll coupons, and the coins it costs (0 unless paid in coins).
    /// </summary>
    public static (RerollPayment Payment, int Coins) Next(int rerollsThisRoll, int coupons, Rulesets.RollRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return rerollsThisRoll < rules.FreeRerollsPerRoll ? (RerollPayment.FreeThisRoll, 0)
            : coupons > 0 ? (RerollPayment.FreeRerollResource, 0)
            : rules.RerollCost.Kind == Rulesets.RerollCostKind.BadEvent ? (RerollPayment.BadEvent, 0)
            : (RerollPayment.Coins, rules.RerollCost.Amount ?? 0);
    }
}
