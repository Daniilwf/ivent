using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Economy;

/// <summary>
/// The player rolls the shop (SPEC «Магазин»): <c>economy.shop.lotsPerRoll</c> personal lots for
/// <c>economy.shop.lotLifetimeMinutes</c>. A free shop roll (the <c>freeShopRolls</c> resource, from the coupon) is
/// spent first, then coins: <c>rollCost</c> plus <c>rerollCostStep</c> for every roll since the price was reset.
/// A new roll replaces the lots still on offer.
/// </summary>
public sealed record RollShop(Guid PlayerId) : ICommand;

/// <summary>The player buys lot number <see cref="Lot"/> (0-based) of their offer while it lasts.</summary>
public sealed record BuyLot(Guid PlayerId, int Lot) : ICommand;

/// <summary>
/// The scheduler's command (D-404): everything whose time has come by the engine's clock — shop offers vanish, effects of
/// hours expire, bets past their deadline are lost. Refused when nothing is due.
/// </summary>
public sealed record FireTimers : ICommand;

/// <summary>
/// A bet (SPEC «Ставки»): <see cref="PlayerId"/> bets <see cref="Stake"/> coins that <see cref="OnPlayerId"/> completes
/// their current run within <see cref="Days"/> days (one of <c>bets.deadlineOptionsDays</c>).
/// </summary>
public sealed record PlaceBet(Guid PlayerId, Guid OnPlayerId, int Days, int Stake) : ICommand;

/// <summary>How a shop roll was paid.</summary>
public enum ShopPayment
{
    /// <summary>One <c>freeShopRolls</c> resource (a coupon used).</summary>
    FreeRoll,

    Coins,
}

/// <summary>The shop rolled <see cref="Lots"/> for the player until <see cref="ExpiresAt"/>; the payment follows as its own event.</summary>
[EventType("shop-rolled")]
public sealed record ShopRolled(Guid PlayerId, EquatableArray<ShopLot> Lots, DateTimeOffset ExpiresAt, ShopPayment Payment, int Price) : IGameEvent;

/// <summary>The player bought lot <see cref="Lot"/>: <see cref="Item"/> goes into the inventory; the coins follow.</summary>
[EventType("lot-bought")]
public sealed record LotBought(Guid PlayerId, int Lot, InventoryObject Item, int Price) : IGameEvent;

/// <summary>The offer's time is over: its lots vanished (SPEC «по истечении лоты исчезают»).</summary>
[EventType("shop-offer-expired")]
public sealed record ShopOfferExpired(Guid PlayerId) : IGameEvent;

/// <summary>A run of the player ended (<c>economy.shop.resetOn</c>): the shop roll price starts over.</summary>
[EventType("shop-price-restarted")]
public sealed record ShopPriceRestarted(Guid PlayerId) : IGameEvent;

/// <summary>The bet was placed; the stake follows as a coins change.</summary>
[EventType("bet-placed")]
public sealed record BetPlaced(Guid PlayerId, Bet Bet) : IGameEvent;

/// <summary>The bet was decided: won (the payout follows as coins), lost (the stake burns) or revoked (the payout is taken back).</summary>
[EventType("bet-settled")]
public sealed record BetSettled(Guid PlayerId, Guid BetId, BetStatus Status, int Payout) : IGameEvent;
