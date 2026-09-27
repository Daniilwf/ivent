using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Inventory;

/// <summary>
/// The player uses an item (SPEC «Окна использования»). <see cref="TargetPlayerId"/> — the target of a <c>chosen</c>
/// selector. <see cref="Choices"/> — the answers to the effect's <c>requestChoice</c> actions in their order, and the
/// object picked by a <c>takeObject</c> with <c>pick: chosen</c> (its instance id): a use is one command, the server
/// checks every answer against the options it computes (D-407).
/// </summary>
public sealed record UseItem(Guid PlayerId, Guid InstanceId, Guid? TargetPlayerId = null, EquatableArray<string> Choices = default) : ICommand;

/// <summary>
/// The admin corrects a player's inventory (SPEC «Админка: ручная правка … инвентаря»): gives an object by its id or
/// removes one by its instance, with a comment in the public log.
/// </summary>
public sealed record AdjustInventory(Guid PlayerId, string? GiveObjectId, Guid? RemoveInstanceId, string Comment) : ICommand;

/// <summary>Where an object came from.</summary>
public enum ObjectSource
{
    /// <summary>A <c>giveObject</c> or <c>spinWheel</c> of an item or an effect.</summary>
    Effect,

    /// <summary>A lot bought in the shop.</summary>
    Shop,

    /// <summary>A shop cell granted it (the coupon, CONTENT.md <c>grants</c>).</summary>
    Cell,

    /// <summary>The admin's correction.</summary>
    Admin,

    /// <summary>Taken from another player by <c>takeObject</c>.</summary>
    Taken,
}

/// <summary>Why an object left the inventory.</summary>
public enum ObjectRemoval
{
    /// <summary>An item used, or an effect or special roll whose last use fired.</summary>
    Used,

    /// <summary>Its runs or hours are over.</summary>
    Expired,

    /// <summary>Destroyed by <c>takeObject</c> with <c>destroy</c>.</summary>
    Destroyed,

    /// <summary>The admin's correction.</summary>
    Admin,
}

/// <summary>The player got <see cref="Item"/>; <see cref="FromPlayerId"/> — the other player behind it, if any.</summary>
[EventType("object-given")]
public sealed record ObjectGiven(Guid PlayerId, InventoryObject Item, ObjectSource Source, Guid? FromPlayerId = null) : IGameEvent;

/// <summary>An object left the player's inventory.</summary>
[EventType("object-removed")]
public sealed record ObjectRemoved(Guid PlayerId, Guid InstanceId, string ObjectId, ObjectRemoval Reason) : IGameEvent;

/// <summary>An object went from <see cref="PlayerId"/> to <see cref="ToPlayerId"/> (<c>takeObject</c> with <c>take</c> or <c>steal</c>).</summary>
[EventType("object-transferred")]
public sealed record ObjectTransferred(Guid PlayerId, Guid ToPlayerId, Guid InstanceId, string ObjectId) : IGameEvent;

/// <summary>
/// An object held changed: a use of an effect fired, a run of its lifetime passed, a note was added or it was turned into
/// another object (<see cref="InventoryObject.ObjectId"/> is then the new one). The object is stored whole, as it is now.
/// </summary>
[EventType("object-changed")]
public sealed record ObjectChanged(Guid PlayerId, InventoryObject Item) : IGameEvent;

/// <summary>An item did not fit: the inventory of <see cref="PlayerId"/> is full (<c>economy.inventoryLimit</c>), the object is lost (D-406).</summary>
[EventType("object-lost")]
public sealed record ObjectLost(Guid PlayerId, string ObjectId) : IGameEvent;

/// <summary>
/// The player used an item: on <see cref="Targets"/> (resolved by the server — a random one included) with the
/// <see cref="Choices"/> they gave. The item's actions follow as their own events.
/// </summary>
[EventType("item-used")]
public sealed record ItemUsed(Guid PlayerId, Guid InstanceId, string ObjectId, EquatableArray<Guid> Targets, EquatableArray<string> Choices) : IGameEvent;

/// <summary>An effect or a special roll of the player fired on its trigger; its actions follow.</summary>
[EventType("effect-triggered")]
public sealed record EffectTriggered(Guid PlayerId, Guid InstanceId, string ObjectId, Trigger Trigger) : IGameEvent;

/// <summary>The dice of a <c>roll</c> action or of an effect's outcomes, each die kept (the result of <c>$roll</c>).</summary>
[EventType("effect-rolled")]
public sealed record EffectRolled(Guid PlayerId, string ObjectId, EquatableArray<Runs.Die> Dice, int Total) : IGameEvent;

/// <summary>
/// A hostile effect of <see cref="FromPlayerId"/>'s <see cref="ObjectId"/> was stopped by the player's interception
/// (<see cref="InstanceId"/>, «Щит»): nothing of it applies to them.
/// </summary>
[EventType("hostile-intercepted")]
public sealed record HostileIntercepted(Guid PlayerId, Guid InstanceId, string ObjectId, Guid FromPlayerId) : IGameEvent;

/// <summary>A hostile effect of another player reached the player (SPEC «Админка считает, сколько враждебных эффектов получил каждый»).</summary>
[EventType("hostile-received")]
public sealed record HostileReceived(Guid PlayerId, Guid FromPlayerId, string ObjectId) : IGameEvent;

/// <summary>A loot wheel was spun for the player and landed on <see cref="ObjectId"/>.</summary>
[EventType("wheel-spun")]
public sealed record WheelSpun(Guid PlayerId, string WheelId, string ObjectId) : IGameEvent;

/// <summary>The player's next roll is changed (<c>modifyNextRoll</c>).</summary>
[EventType("next-roll-modified")]
public sealed record NextRollModified(Guid PlayerId, RollModifier Modifier) : IGameEvent;

/// <summary>
/// A roll from Idle took the pending roll changes (<see cref="Modifiers"/>): they hold for this roll and its rerolls
/// until the run starts (D-405).
/// </summary>
[EventType("roll-modifiers-applied")]
public sealed record RollModifiersApplied(Guid PlayerId, EquatableArray<RollModifier> Modifiers) : IGameEvent;

/// <summary>The player's next throw for a completion is changed (<c>modifyDice</c> with <c>when: next</c>).</summary>
[EventType("next-dice-modified")]
public sealed record NextDiceModified(Guid PlayerId, DiceModifier Modifier) : IGameEvent;

/// <summary>The admin's correction of the inventory: the comment in the log; the change follows as its own event.</summary>
[EventType("inventory-adjusted")]
public sealed record InventoryAdjusted(Guid PlayerId, string Comment) : IGameEvent;
