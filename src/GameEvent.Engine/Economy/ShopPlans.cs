using GameEvent.Engine.Inventory;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Economy;

/// <summary>
/// What the shop screen offers (D-443): how the next roll is paid and what it costs, the free rolls held, why a roll is
/// refused now (the engine's code: the mechanic off, the deadline, the frozen first, debt or too few coins), and the offer
/// while it lasts by the server's clock — an offer past its time is gone even before the timer fires (D-404).
/// </summary>
public sealed record ShopPlan(ShopPayment Payment, int Price, int FreeRolls, string? Refusal, ShopOffer? Offer);

public static class ShopPlans
{
    /// <summary>The shop of <paramref name="playerId"/> at <paramref name="now"/>; null for a player who is not in the season.</summary>
    public static ShopPlan? Of(SeasonState state, DateTimeOffset now, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Players.TryGetValue(playerId, out var player))
        {
            return null;
        }

        var (payment, price) = Shop.NextRoll(player, state.Rules.Economy.Shop);
        var refusal = ItemUse.Guard(state, playerId, now, f => f.Shop)?.Rejection?.Code
            ?? (payment == ShopPayment.Coins ? Shop.Unaffordable(player, price)?.Rejection?.Code : null);
        var offer = player.Wallet.Shop is { } shop && now < shop.ExpiresAt ? shop : null;
        return new ShopPlan(payment, price, player.Resources[Shop.FreeShopRollsResource], refusal, offer);
    }
}
