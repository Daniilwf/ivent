using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Inventory;

/// <summary>
/// One object a player holds (SPEC «InventoryObject»): an item, an effect or a special roll, by its definition.
/// <see cref="Params"/> are the values its <c>$parameters</c> got when it was given (<c>forced-genre</c>: the tag).
/// The lifetime (SPEC «Срок жизни эффектов») is what is left of it: <see cref="UsesLeft"/>, <see cref="RunsLeft"/>,
/// <see cref="ExpiresAt"/>; none of them — until it fires once (<c>untilTriggered</c>) or for the season.
/// <see cref="FromPlayerId"/> — who put it on the player, when that was another player (a hostile effect).
/// <see cref="Acquired"/> — the number of the player's acquisition, so objects apply in the order they came (SPEC
/// «Спецролл … по порядку получения»).
/// </summary>
public sealed record InventoryObject(
    Guid InstanceId,
    string ObjectId,
    ObjectKind Kind,
    long Acquired,
    bool Hostile = false,
    Guid? FromPlayerId = null,
    ContentParamDictionary? Params = null,
    int? UsesLeft = null,
    int? RunsLeft = null,
    DateTimeOffset? ExpiresAt = null,
    EquatableArray<string> Notes = default);

/// <summary>
/// A change of the next roll by an item or an effect (CONTENT.md <c>modifyNextRoll</c>): a filter with its parameters
/// resolved, and the number of games to choose from. <see cref="ObjectId"/> — whose change it is, for «не стакается».
/// </summary>
public sealed record RollModifier(string ObjectId, GameFilterSpec? Filter, int? ChoiceCount, bool Hostile = false);

/// <summary>
/// A change of the next throw for a completion (CONTENT.md <c>modifyDice</c> with <c>when: next</c>), waiting for it:
/// the stage and the value with its references resolved (dice are rolled when the throw happens).
/// </summary>
public sealed record DiceModifier(string ObjectId, DiceStage Stage, ContentValue Value, bool Hostile = false);

/// <summary>A lot of the personal shop offer (SPEC «Магазин»): the object, its price in coins, whether it was bought.</summary>
public sealed record ShopLot(string ObjectId, Rarity Rarity, int Price, bool Sold = false);

/// <summary>The personal shop offer: its lots, when they vanish (the server's clock decides, D-404).</summary>
public sealed record ShopOffer(EquatableArray<ShopLot> Lots, DateTimeOffset ExpiresAt);

public enum BetStatus
{
    Open,
    Won,
    Lost,

    /// <summary>Won, then the run's proof was rejected: the payout is taken back (SPEC «Реджект пруфа после выплаты»).</summary>
    Revoked,
}

/// <summary>
/// A bet of the player (SPEC «Ставки»): that <see cref="OnPlayerId"/> completes run <see cref="RunId"/> by
/// <see cref="Deadline"/>. <see cref="Stake"/> coins went into the system's pledge; the win is
/// ⌊stake × <see cref="Multiplier"/>⌋ (<see cref="Payout"/> once won).
/// </summary>
public sealed record Bet(
    Guid BetId,
    Guid OnPlayerId,
    Guid RunId,
    DateTimeOffset PlacedAt,
    DateTimeOffset Deadline,
    int Stake,
    decimal Multiplier,
    BetStatus Status = BetStatus.Open,
    int Payout = 0);

/// <summary>
/// What a player has in the economy of the season beyond points and coins (D-401): the inventory, the pending changes of
/// the next roll and throw, the roll changes of the roll in progress, the shop offer and its reroll count, the bets and the
/// hostile effects received. Kept inside the player, so an undo restores it with the player (D-104). Every field is
/// written only when it holds something; a player with nothing has no economy (null), so logs and snapshots of
/// players without the economy stay as they were.
/// </summary>
public sealed record PlayerEconomy
{
    public static PlayerEconomy Empty { get; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<InventoryObject> Inventory { get; init; }

    /// <summary>How many objects the player has ever got: the order of acquisition (<see cref="InventoryObject.Acquired"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Acquisitions { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<RollModifier> NextRoll { get; init; }

    /// <summary>The changes of the roll in progress: fixed at the roll from Idle, kept for its rerolls, dropped at the start (D-405).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<RollModifier> CurrentRoll { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<DiceModifier> NextDice { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShopOffer? Shop { get; init; }

    /// <summary>Shop rolls since the price was last reset (SPEC «Реролл магазина … каждый раз дороже»).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ShopRolls { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<Bet> Bets { get; init; }

    /// <summary>Hostile effects that reached the player this season (SPEC «Админка считает…», the <c>hostileReceived</c> statistic).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HostileReceived { get; init; }

    [JsonIgnore]
    public bool IsEmpty => this == Empty;

    /// <summary>Items count against <c>economy.inventoryLimit</c>; effects and special rolls are states, not things held (D-406).</summary>
    [JsonIgnore]
    public int Items => Inventory.Count(o => o.Kind == ObjectKind.Item);

    public InventoryObject? Find(Guid instanceId) => Inventory.FirstOrDefault(o => o.InstanceId == instanceId);
}
