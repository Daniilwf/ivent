using GameEvent.Engine.Inventory;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Economy;

/// <summary>
/// What the shop screen offers (D-443): how the next roll is paid and what it costs, the free rolls held, why a roll is
/// refused now (the engine's code: the mechanic off, the deadline, the frozen first, debt or too few coins), and the offer
/// while it lasts by the server's clock — an offer past its time is gone even before the timer fires (D-404).
/// <c>LotRefusals</c> — for each lot of the offer, in order, why a purchase is refused now (sold, too few coins, the
/// inventory full…), null when it is sold to the player (D-447).
/// </summary>
public sealed record ShopPlan(ShopPayment Payment, int Price, int FreeRolls, string? Refusal, ShopOffer? Offer, IReadOnlyList<string?> LotRefusals);

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
        var guard = ItemUse.Guard(state, playerId, now, f => f.Shop)?.Rejection?.Code;
        var refusal = guard
            ?? (payment == ShopPayment.Coins ? Shop.Unaffordable(player, price)?.Rejection?.Code : null);
        var offer = player.Wallet.Shop is { } shop && now < shop.ExpiresAt ? shop : null;
        IReadOnlyList<string?> lots = offer is null
            ? []
            : [.. offer.Lots.Select((_, i) => guard ?? Shop.LotRefusal(state, player, now, i)?.Rejection?.Code)];
        return new ShopPlan(payment, price, player.Resources[Shop.FreeShopRollsResource], refusal, offer, lots);
    }
}
