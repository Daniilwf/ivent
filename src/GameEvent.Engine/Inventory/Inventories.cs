using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Inventory;

/// <summary>The rules of holding objects (D-401, D-406) and the fold of the inventory events.</summary>
internal static class Inventories
{
    /// <summary>
    /// The events of giving <paramref name="definition"/> to <paramref name="playerId"/>: an object with its lifetime
    /// started now; an item that does not fit is lost (D-406); an effect or special roll without automation goes straight
    /// to manual resolution (D-410).
    /// </summary>
    public static IGameEvent Give(
        SeasonState state,
        EngineContext context,
        Guid playerId,
        ObjectDefinition definition,
        ContentParamDictionary? parameters,
        ObjectSource source,
        Guid? fromPlayerId)
    {
        var player = state.Players[playerId];
        if (definition.Kind == ObjectKind.Item && player.Wallet.Items >= state.Rules.Economy.InventoryLimit)
        {
            return new ObjectLost(playerId, definition.Id);
        }

        if (definition.Kind != ObjectKind.Item && definition.Effect is null)
        {
            return new ManualEffectCreated(context.Ids.NewId(), playerId, DrawEvent: null, ManualEffectSource.Item, RunId: null) { ObjectId = definition.Id };
        }

        var from = fromPlayerId == playerId ? null : fromPlayerId;
        return new ObjectGiven(playerId, New(state, context, playerId, definition, parameters, from), source, from);
    }

    /// <summary>A new object of <paramref name="definition"/> for <paramref name="playerId"/>, its lifetime starting now.</summary>
    public static InventoryObject New(
        SeasonState state, EngineContext context, Guid playerId, ObjectDefinition definition, ContentParamDictionary? parameters, Guid? fromPlayerId)
    {
        var duration = definition.Effect?.Duration;
        return new InventoryObject(
            context.Ids.NewId(),
            definition.Id,
            definition.Kind,
            state.Players[playerId].Wallet.Acquisitions + 1,
            definition.Hostile && fromPlayerId is not null && fromPlayerId != playerId,
            fromPlayerId == playerId ? null : fromPlayerId,
            parameters is { Count: > 0 } ? parameters : null,
            duration?.Uses,
            duration?.Runs,
            duration?.Hours is { } hours ? context.Clock.UtcNow.AddHours(hours) : null,
            Counter: definition.Effect?.Counter?.Start);
    }

    /// <summary>
    /// After an effect or special roll fired: one use less, gone after its last use or when it lives until it fires
    /// (SPEC «Срок жизни эффектов»).
    /// </summary>
    public static IGameEvent? AfterFiring(SeasonState state, Guid playerId, InventoryObject item)
    {
        var definition = state.Catalog.Get(item.ObjectId);
        if (definition.Effect?.Duration?.UntilTriggered == true || item.UsesLeft is <= 1)
        {
            return new ObjectRemoved(playerId, item.InstanceId, item.ObjectId, ObjectRemoval.Used);
        }

        return item.UsesLeft is { } uses ? new ObjectChanged(playerId, item with { UsesLeft = uses - 1 }) : null;
    }

    public static SeasonState Apply(SeasonState state, ObjectGiven e)
    {
        // D-421: what a cell grants while the player's last move is a run's own move belongs to that run — a reject takes
        // it back (the same sign as the points bonus of the cell, D-327)
        if (e.Source == ObjectSource.Cell && state.Players[e.PlayerId].LastMoveRunId is { } mover && state.Runs.TryGetValue(mover, out var run))
        {
            state = state with { Runs = state.Runs.SetItem(mover, run with { CellObjects = [.. run.CellObjects, e.Item.InstanceId] }) };
        }

        return Update(state, e.PlayerId, w => w with { Inventory = [.. w.Inventory, e.Item], Acquisitions = Math.Max(w.Acquisitions, e.Item.Acquired) });
    }

    public static SeasonState Apply(SeasonState state, ObjectRemoved e) =>
        Update(state, e.PlayerId, w => w with { Inventory = Without(w, e.InstanceId) });

    public static SeasonState Apply(SeasonState state, ObjectChanged e) =>
        Update(state, e.PlayerId, w => w with
        {
            Inventory = [.. w.Inventory.Select(o => o.InstanceId == e.Item.InstanceId ? e.Item : o)],
        });

    public static SeasonState Apply(SeasonState state, ObjectTransferred e)
    {
        var item = state.Players[e.PlayerId].Wallet.Find(e.InstanceId)
            ?? throw new InvalidOperationException($"Object {e.InstanceId} is not held by {e.PlayerId}.");
        state = Update(state, e.PlayerId, w => w with { Inventory = Without(w, e.InstanceId) });
        // Handed over (D-478): the giver's doing, like an object he put on the receiver; taken: the taker's own
        var from = e.Given ? e.PlayerId : (Guid?)null;
        var hostile = e.Given && state.Catalog.Get(item.ObjectId).Hostile;
        return Update(state, e.ToPlayerId, w => w with
        {
            Inventory = [.. w.Inventory, item with { Acquired = w.Acquisitions + 1, FromPlayerId = from, Hostile = hostile }],
            Acquisitions = w.Acquisitions + 1,
        });
    }

    public static SeasonState Apply(SeasonState state, HostileReceived e) =>
        Update(state, e.PlayerId, w => w with { HostileReceived = w.HostileReceived + 1 });

    public static SeasonState Apply(SeasonState state, NextRollModified e) =>
        Update(state, e.PlayerId, w => w with { NextRoll = [.. w.NextRoll, e.Modifier] });

    public static SeasonState Apply(SeasonState state, RollModifiersApplied e) =>
        Update(state, e.PlayerId, w => w with { NextRoll = e.Waiting, CurrentRoll = e.Modifiers });

    public static SeasonState Apply(SeasonState state, NextDiceModified e) =>
        Update(state, e.PlayerId, w => w with { NextDice = [.. w.NextDice, e.Modifier] });

    private static EquatableArray<InventoryObject> Without(PlayerEconomy wallet, Guid instanceId) =>
        wallet.Find(instanceId) is null
            ? throw new InvalidOperationException($"Object {instanceId} is not in the inventory.")
            : [.. wallet.Inventory.Where(o => o.InstanceId != instanceId)];

    /// <summary>Changes a player's economy; an economy left empty is stored as none, so a replay compares equal (D-401).</summary>
    public static SeasonState Update(SeasonState state, Guid playerId, Func<PlayerEconomy, PlayerEconomy> change)
    {
        var player = state.Players[playerId];
        var economy = Normalized(change(player.Wallet));
        return state with { Players = state.Players.SetItem(playerId, player with { Economy = economy.IsEmpty ? null : economy }) };
    }

    // Empty lists are stored as default lists, so an economy compares and writes the same however it was built.
    private static PlayerEconomy Normalized(PlayerEconomy e) =>
        e with
        {
            Inventory = e.Inventory.Count == 0 ? default : e.Inventory,
            NextRoll = e.NextRoll.Count == 0 ? default : e.NextRoll,
            CurrentRoll = e.CurrentRoll.Count == 0 ? default : e.CurrentRoll,
            NextDice = e.NextDice.Count == 0 ? default : e.NextDice,
            Bets = e.Bets.Count == 0 ? default : e.Bets,
        };
}

/// <summary>Passive protection (SPEC «Защита только пассивная», CONTENT.md «Щит»): an effect that stops a hostile one.</summary>
internal static class Interception
{
    /// <summary>The player's first interception of a hostile effect, in the order it came; null when they have none.</summary>
    public static InventoryObject? Find(SeasonState state, Guid playerId) => Find(state, playerId, Intercept.Hostile);

    /// <summary>
    /// The player's oldest object that stops <paramref name="threat"/> (D-484 (3): several interceptors — the oldest first);
    /// null when they have none.
    /// </summary>
    public static InventoryObject? Find(SeasonState state, Guid playerId, Intercept threat) =>
        state.Players[playerId].Wallet.Inventory
            .Where(o => state.Catalog.Find(o.ObjectId)?.Effect is { Intercept: { } stops, Trigger: Trigger.HostileIncoming } && stops.HasFlag(threat))
            .OrderBy(o => o.Acquired)
            .FirstOrDefault();

    public static IEnumerable<IGameEvent> Stop(SeasonState state, Guid playerId, InventoryObject shield, string objectId, Guid fromPlayerId)
    {
        yield return new HostileIntercepted(playerId, shield.InstanceId, objectId, fromPlayerId);
        if (Inventories.AfterFiring(state, playerId, shield) is { } spent)
        {
            yield return spent;
        }
    }
}
