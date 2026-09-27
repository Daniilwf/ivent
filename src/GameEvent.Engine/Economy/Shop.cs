using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Economy;

/// <summary>
/// The personal shop (SPEC «Магазин», D-403): a roll gives <c>lotsPerRoll</c> lots drawn by the rarity weights among the
/// items for sale, priced by the zone the player stands in, for <c>lotLifetimeMinutes</c>. Every roll within one game costs
/// more; the price starts over when a run ends (<c>resetOn</c>). Nothing is bought in debt (SPEC «в минусе покупать нельзя»).
/// </summary>
internal static class Shop
{
    /// <summary>A free shop roll: the shop coupon gives it (CONTENT.md cell «shop-coupon», D-403).</summary>
    public const string FreeShopRollsResource = "freeShopRolls";

    /// <summary>What the next shop roll of <paramref name="player"/> costs and how it is paid.</summary>
    public static (ShopPayment Payment, int Price) NextRoll(SeasonPlayer player, Rulesets.ShopRules rules)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(rules);
        return player.Resources[FreeShopRollsResource] > 0
            ? (ShopPayment.FreeRoll, 0)
            : (ShopPayment.Coins, rules.RollCost + (rules.RerollCostStep * player.Wallet.ShopRolls));
    }

    public static Decision Decide(SeasonState state, RollShop command, EngineContext context)
    {
        if (ItemUse.Guard(state, command.PlayerId, context, f => f.Shop) is { } refused)
        {
            return refused;
        }

        var player = state.Players[command.PlayerId];
        var (payment, price) = NextRoll(player, state.Rules.Economy.Shop);
        if (payment == ShopPayment.Coins && Unaffordable(player, price) is { } poor)
        {
            return poor;
        }

        var lots = Draw(state, player, context);
        if (lots.Count == 0)
        {
            return Decision.Reject(RejectionCodes.ShopEmpty, "Nothing is for sale in the season's content.");
        }

        var expires = context.Clock.UtcNow.AddMinutes(state.Rules.Economy.Shop.LotLifetimeMinutes);
        IGameEvent? paid = payment == ShopPayment.FreeRoll
            ? new ResourceChanged(player.PlayerId, FreeShopRollsResource, -1, ResourceReason.ShopRoll)
            : price != 0 ? new CoinsChanged(player.PlayerId, -price, CoinsReason.ShopRoll, RunId: null) : null;
        var rolled = new ShopRolled(player.PlayerId, [.. lots], expires, payment, price);
        return Decision.Accept(paid is null ? [rolled] : [rolled, paid]);
    }

    public static Decision Decide(SeasonState state, BuyLot command, EngineContext context)
    {
        if (ItemUse.Guard(state, command.PlayerId, context, f => f.Shop) is { } refused)
        {
            return refused;
        }

        var player = state.Players[command.PlayerId];
        if (player.Wallet.Shop is not { } offer)
        {
            return Decision.Reject(RejectionCodes.ShopNoOffer, "Roll the shop first.");
        }

        // The server's clock decides, even before the timer has fired (SPEC «Время считает сервер»).
        if (context.Clock.UtcNow >= offer.ExpiresAt)
        {
            return Decision.Reject(RejectionCodes.ShopOfferExpired, "The lots have vanished.");
        }

        if (command.Lot < 0 || command.Lot >= offer.Lots.Count)
        {
            return Decision.Reject(RejectionCodes.ShopUnknownLot, $"The offer has lots 0–{offer.Lots.Count - 1}.");
        }

        var lot = offer.Lots[command.Lot];
        if (lot.Sold)
        {
            return Decision.Reject(RejectionCodes.ShopLotSold, "This lot is already bought.");
        }

        if (Unaffordable(player, lot.Price) is { } poor)
        {
            return poor;
        }

        if (player.Wallet.Items >= state.Rules.Economy.InventoryLimit)
        {
            return Decision.Reject(RejectionCodes.InventoryFull, $"The inventory holds at most {state.Rules.Economy.InventoryLimit} items.");
        }

        var item = Inventories.New(state, context, player.PlayerId, state.Catalog.Get(lot.ObjectId), null, fromPlayerId: null);
        var bought = new LotBought(player.PlayerId, command.Lot, item, lot.Price);
        return Decision.Accept(lot.Price == 0 ? [bought] : [bought, new CoinsChanged(player.PlayerId, -lot.Price, CoinsReason.Purchase, RunId: null)]);
    }

    /// <summary>A purchase needs coins not in debt and enough of them (SPEC «Минус»).</summary>
    public static Decision? Unaffordable(SeasonPlayer player, int price) =>
        player.Coins < 0
            ? Decision.Reject(RejectionCodes.CoinsInDebt, $"The player is in debt ({player.Coins} coins): nothing is bought.")
            : player.Coins < price
                ? Decision.Reject(RejectionCodes.NotEnoughCoins, $"It costs {price} coins, the player has {player.Coins}.")
                : null;

    // Lots by rarity weight among the items for sale, without repeats while there are others; priced by the zone.
    private static List<ShopLot> Draw(SeasonState state, SeasonPlayer player, EngineContext context)
    {
        var weights = state.Rules.Economy.RarityWeights;
        var left = state.Catalog.Objects.Values
            .Where(e => !e.Deleted && e.Definition is { Kind: ObjectKind.Item, Price: not null })
            .Select(e => e.Definition)
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
        var multiplier = state.Map.ZoneOf(player.CellId)?.ShopPriceMultiplier ?? 1m;
        var lots = new List<ShopLot>();
        while (lots.Count < state.Rules.Economy.Shop.LotsPerRoll && left.Count > 0)
        {
            var rarities = left.Select(d => d.Rarity ?? Rarity.Common).Distinct().Order()
                .Select(r => (Rarity: r, Weight: r switch { Rarity.Epic => weights.Epic, Rarity.Legendary => weights.Legendary, _ => weights.Common }))
                .Where(r => r.Weight > 0)
                .ToList();
            if (rarities.Count == 0)
            {
                break;
            }

            var ticket = context.Random.NextInt(0, rarities.Sum(r => r.Weight));
            var rarity = rarities.First(r => (ticket -= r.Weight) < 0).Rarity;
            var ofRarity = left.Where(d => (d.Rarity ?? Rarity.Common) == rarity).ToList();
            var definition = ofRarity[context.Random.NextInt(0, ofRarity.Count)];
            left.Remove(definition);
            lots.Add(new ShopLot(definition.Id, rarity, (int)Math.Round(definition.Price!.Value * multiplier, MidpointRounding.AwayFromZero)));
        }

        return lots;
    }

    public static SeasonState Apply(SeasonState state, ShopRolled e) =>
        Inventories.Update(state, e.PlayerId, w => w with { Shop = new ShopOffer(e.Lots, e.ExpiresAt), ShopRolls = w.ShopRolls + 1 });

    public static SeasonState Apply(SeasonState state, LotBought e) =>
        Inventories.Update(state, e.PlayerId, w => w with
        {
            Shop = w.Shop! with { Lots = [.. w.Shop.Lots.Select((lot, i) => i == e.Lot ? lot with { Sold = true } : lot)] },
            Inventory = [.. w.Inventory, e.Item],
            Acquisitions = Math.Max(w.Acquisitions, e.Item.Acquired),
        });

    public static SeasonState Apply(SeasonState state, ShopOfferExpired e) =>
        Inventories.Update(state, e.PlayerId, w => w with { Shop = null });

    public static SeasonState Apply(SeasonState state, ShopPriceRestarted e) =>
        Inventories.Update(state, e.PlayerId, w => w with { ShopRolls = 0 });
}
