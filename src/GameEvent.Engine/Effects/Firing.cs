using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>
/// Effects and special rolls that act on their own (SPEC «Эффект … действует сам»): the owner's objects subscribed to a
/// trigger fire in the order they came, when their condition holds; a use is spent each time (D-412). The first finisher's
/// effects are frozen (SPEC «эффекты обнуляются»): they do not fire.
/// </summary>
internal static class Firing
{
    /// <summary>The owner's objects that fire on <paramref name="trigger"/>, oldest first.</summary>
    public static IReadOnlyList<InventoryObject> Subscribed(SeasonState state, Guid ownerId, Trigger trigger, ObjectKind? kind = null) =>
        Targets.IsFirst(state, ownerId)
            ? []
            : [.. state.Players[ownerId].Wallet.Inventory
                .Where(o => o.Kind != ObjectKind.Item && (kind is null || o.Kind == kind))
                .Where(o => state.Catalog.Find(o.ObjectId)?.Effect is { Intercept: null } effect && effect.Trigger == trigger)
                .OrderBy(o => o.Acquired)];

    /// <summary>Fires <paramref name="item"/> of <paramref name="ownerId"/>; null when its condition does not hold.</summary>
    public static EffectRun? Fire(
        SeasonState state, EngineContext context, Guid ownerId, InventoryObject item, Trigger trigger, Guid? runId = null, DiceScope dice = DiceScope.Pending)
    {
        var definition = state.Catalog.Get(item.ObjectId);
        var effect = definition.Effect!;
        if (effect.Condition is { } condition && !Conditions.Hold(state, context, ownerId, condition, runId))
        {
            return null;
        }

        var run = new EffectRun(state, context, ownerId, item.ObjectId, definition.Hostile, item.Params, dice: dice, runId: runId);
        run.Emit(new EffectTriggered(ownerId, item.InstanceId, item.ObjectId, trigger));
        run.Run(effect, Targets.ForTriggered(run.State, context, definition, ownerId));

        // The object may be gone already (an effect that takes its own kind); a use is spent only on one still held.
        if (run.State.Players[ownerId].Wallet.Find(item.InstanceId) is { } still && Inventories.AfterFiring(run.State, ownerId, still) is { } spent)
        {
            run.Emit(spent);
        }

        return run;
    }

    /// <summary>Fires every subscribed object in turn on the state the ones before left; the events in order.</summary>
    public static (IReadOnlyList<IGameEvent> Events, SeasonState State) All(
        SeasonState state, EngineContext context, Guid ownerId, Trigger trigger, Guid? runId = null, DiceScope dice = DiceScope.Pending)
    {
        var events = new List<IGameEvent>();
        foreach (var item in Subscribed(state, ownerId, trigger))
        {
            if (state.Players[ownerId].Wallet.Find(item.InstanceId) is not { } held || Fire(state, context, ownerId, held, trigger, runId, dice) is not { } run)
            {
                continue;
            }

            events.AddRange(run.Events);
            state = run.State;
        }

        return (events, state);
    }
}
