using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>
/// Effects and special rolls that act on their own (SPEC «Эффект … действует сам»): the owner's objects subscribed to a
/// trigger fire in the order they came, when their condition holds; a use is spent each time (D-412). The first finisher's
/// effects are frozen (SPEC «эффекты обнуляются»): they do not fire. A later finisher's hostile objects from others that
/// change the choice of the game (a forced genre) do not fire either and stay where they are (D-425).
/// </summary>
internal static class Firing
{
    /// <summary>Where in its command a firing happens (invariant 8): the chain level and the events written before it.</summary>
    public static ChainPoint InCommand { get; } = new(0, 0);

    /// <summary>The owner's objects that fire on <paramref name="trigger"/>, oldest first.</summary>
    public static IReadOnlyList<InventoryObject> Subscribed(SeasonState state, Guid ownerId, Trigger trigger, ObjectKind? kind = null) =>
        Targets.IsFirst(state, ownerId)
            ? []
            : [.. state.Players[ownerId].Wallet.Inventory
                .Where(o => o.Kind != ObjectKind.Item && (kind is null || o.Kind == kind))
                .Where(o => state.Catalog.Find(o.ObjectId)?.Effect is { Intercept: null } effect && effect.Trigger == trigger
                    && !(o.Hostile && Targets.ChangesTheGame(effect) && Targets.IsLaterFinisher(state, ownerId)))
                .OrderBy(o => o.Acquired)];

    /// <summary>Fires <paramref name="item"/> of <paramref name="ownerId"/>; null when its condition does not hold.</summary>
    public static EffectRun? Fire(
        SeasonState state, EngineContext context, Guid ownerId, InventoryObject item, Trigger trigger, Guid? runId = null, DiceScope dice = DiceScope.Pending,
        ChainPoint? at = null)
    {
        var point = at ?? InCommand;
        var definition = state.Catalog.Get(item.ObjectId);
        var effect = definition.Effect!;
        if (effect.Condition is { } condition && !Conditions.Hold(state, context, ownerId, condition, runId))
        {
            return null;
        }

        var run = new EffectRun(
            state, context, ownerId, item.ObjectId, definition.Hostile, item.Params, dice: dice, runId: runId, depth: point.Depth, eventsBefore: point.EventsBefore)
        {
            EventKind = item.EventKind,
        };
        run.Emit(new EffectTriggered(ownerId, item.InstanceId, item.ObjectId, trigger));

        // A countdown lowers its counter instead of acting, until it reaches zero (D-479)
        if (effect.Counter is not null && item.Counter is > 1)
        {
            run.Emit(new ObjectChanged(ownerId, item with { Counter = item.Counter - 1 }));
            return run;
        }

        run.Run(effect, Targets.ForTriggered(run.State, context, definition, ownerId));

        // The object may be gone already (an effect that takes its own kind); a use is spent only on one still held. After a
        // cut nothing more is written: the cut is the last event (AU4-1).
        if (run.Cut is null && run.State.Players[ownerId].Wallet.Find(item.InstanceId) is { } still && Inventories.AfterFiring(run.State, ownerId, still) is { } spent)
        {
            run.Emit(spent);
        }

        return run;
    }

    /// <summary>
    /// A countdown that reached zero outside its trigger (a potion of time, D-479): its actions run now for its owner, and
    /// it is spent as after a firing. The events go into <paramref name="parent"/>. It runs one level deeper than the run
    /// that brought it to zero and counts towards the command's events: past the chain limits it is cut (AU4-1, invariant 8).
    /// </summary>
    public static void GoOff(EffectRun parent, Guid ownerId, InventoryObject item)
    {
        var depth = parent.Depth + 1;
        if (ChainLimits.Of(parent.State).Cut(depth, parent.EventsSoFar, 1) is { } cut)
        {
            parent.Emit(cut);
            return;
        }

        var definition = parent.State.Catalog.Get(item.ObjectId);
        var run = new EffectRun(
            parent.State, parent.Context, ownerId, item.ObjectId, definition.Hostile, item.Params, depth: depth, eventsBefore: parent.EventsSoFar)
        {
            EventKind = item.EventKind,
        };
        run.Run(definition.Effect!, Targets.ForTriggered(run.State, parent.Context, definition, ownerId));
        if (run.Cut is null && run.State.Players[ownerId].Wallet.Find(item.InstanceId) is { } still && Inventories.AfterFiring(run.State, ownerId, still) is { } spent)
        {
            run.Emit(spent);
        }

        parent.Emit(run.Events);
    }

    /// <summary>
    /// Fires every subscribed object in turn on the state the ones before left: the events of each firing, in order — a
    /// firing is written whole or not at all (AU4-4). A firing cut inside ends the list (AU4-1).
    /// </summary>
    public static (IReadOnlyList<IReadOnlyList<IGameEvent>> Firings, SeasonState State) Each(
        SeasonState state, EngineContext context, Guid ownerId, Trigger trigger, Guid? runId, DiceScope dice, ChainPoint at)
    {
        var firings = new List<IReadOnlyList<IGameEvent>>();
        var eventsBefore = at.EventsBefore;
        foreach (var item in Subscribed(state, ownerId, trigger))
        {
            if (state.Players[ownerId].Wallet.Find(item.InstanceId) is not { } held
                || Fire(state, context, ownerId, held, trigger, runId, dice, at with { EventsBefore = eventsBefore }) is not { } run)
            {
                continue;
            }

            firings.Add(run.Events);
            eventsBefore += run.Events.Count;
            state = run.State;
            if (run.Cut is not null)
            {
                break;
            }
        }

        return (firings, state);
    }
}

/// <summary>Where in its command an effect fires (invariant 8): the chain level and the events the command wrote before it.</summary>
internal sealed record ChainPoint(int Depth, int EventsBefore);
