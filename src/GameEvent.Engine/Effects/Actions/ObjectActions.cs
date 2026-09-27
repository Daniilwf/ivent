using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Effects.Actions;

/// <summary>Which objects of an inventory an action may take or change (CONTENT.md <c>filter</c>), in the order they came.</summary>
internal static class ObjectFilters
{
    public static IReadOnlyList<InventoryObject> Matching(EffectRun run, Guid playerId, ObjectFilterSpec? filter) =>
        [.. run.State.Players[playerId].Wallet.Inventory
            .Where(o => filter is null || Matches(run.State.Catalog.Get(o.ObjectId), o, filter))
            .OrderBy(o => o.Acquired)];

    private static bool Matches(ObjectDefinition definition, InventoryObject item, ObjectFilterSpec filter) =>
        (filter.Kind is not { } kind || item.Kind == kind)
        && (filter.Rarity is not { } rarity || definition.Rarity == rarity)
        && (filter.ObjectId is not { } id || item.ObjectId == id)
        && (filter.Hostile is not { } hostile || definition.Hostile == hostile);

    /// <summary>One of <paramref name="candidates"/>: at random, or the one the player named (its instance id).</summary>
    public static InventoryObject? Pick(EffectRun run, IReadOnlyList<InventoryObject> candidates, Pick pick)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (pick == Content.Pick.Random)
        {
            return candidates[run.Context.Random.NextInt(0, candidates.Count)];
        }

        return run.NextChoice([.. candidates.Select(o => o.InstanceId.ToString("N"))]) is { } chosen
            ? candidates.Single(o => o.InstanceId.ToString("N") == chosen)
            : null;
    }
}

/// <summary><c>giveObject</c>: a new object of the current content, its parameters resolved now (<c>$choice</c> → «Horror»).</summary>
internal sealed class GiveObjectHandler : ActionHandler<GiveObjectAction>
{
    protected override void Execute(GiveObjectAction action, EffectRun run)
    {
        if (run.State.Catalog.Live(action.ObjectId) is not { } definition)
        {
            return;
        }

        var parameters = action.Params is { } given
            ? new ContentParamDictionary(given.Select(p => KeyValuePair.Create(p.Key, run.Text(p.Value))))
            : null;
        run.Emit(Inventories.Give(run.State, run.Context, run.ActionTarget(action), definition, parameters, ObjectSource.Effect, run.UserId));
    }
}

/// <summary>
/// <c>takeObject</c>: <c>take</c> and <c>steal</c> move the object to the one who used the effect (an item that does not
/// fit is lost, D-406), <c>destroy</c> removes it.
/// </summary>
internal sealed class TakeObjectHandler : ActionHandler<TakeObjectAction>
{
    protected override void Execute(TakeObjectAction action, EffectRun run)
    {
        var from = run.ActionTarget(action);
        if (ObjectFilters.Pick(run, ObjectFilters.Matching(run, from, action.Filter), action.Pick) is not { } item)
        {
            return;
        }

        if (action.Mode == TakeMode.Destroy)
        {
            run.Emit(new ObjectRemoved(from, item.InstanceId, item.ObjectId, ObjectRemoval.Destroyed));
            return;
        }

        var to = run.UserId;
        if (to == from)
        {
            return;
        }

        if (item.Kind == ObjectKind.Item && run.State.Players[to].Wallet.Items >= run.State.Rules.Economy.InventoryLimit)
        {
            run.Emit(new ObjectRemoved(from, item.InstanceId, item.ObjectId, ObjectRemoval.Destroyed));
            run.Emit(new ObjectLost(to, item.ObjectId));
            return;
        }

        run.Emit(new ObjectTransferred(from, to, item.InstanceId, item.ObjectId));
    }
}

/// <summary><c>transformObject</c>: one matching object at random turns into another (its lifetime starts anew) or gets a note.</summary>
internal sealed class TransformObjectHandler : ActionHandler<TransformObjectAction>
{
    protected override void Execute(TransformObjectAction action, EffectRun run)
    {
        var owner = run.ActionTarget(action);
        if (ObjectFilters.Pick(run, ObjectFilters.Matching(run, owner, action.Filter), Pick.Random) is not { } item)
        {
            return;
        }

        if (action.Mode == TransformMode.Annotate)
        {
            run.Emit(new ObjectChanged(owner, item with { Notes = [.. item.Notes, run.Text(action.Note!)] }));
            return;
        }

        if (run.State.Catalog.Live(action.Into!) is not { } into)
        {
            return;
        }

        var fresh = Inventories.New(run.State, run.Context, owner, into, item.Params, item.FromPlayerId);
        run.Emit(new ObjectChanged(owner, fresh with { InstanceId = item.InstanceId, Acquired = item.Acquired, Notes = item.Notes }));
    }
}

/// <summary>
/// <c>drawEvent</c>: while <c>features.events</c> is off (stage 5) the event is drawn by hand — a manual effect «draw a
/// good/bad event» (D-10, D-410).
/// </summary>
internal sealed class DrawEventHandler : ActionHandler<DrawEventAction>
{
    public static EventKind? KindOf(string deck) => deck switch
    {
        "good" => EventKind.Good,
        "bad" => EventKind.Bad,
        _ => null,
    };

    protected override void Execute(DrawEventAction action, EffectRun run)
    {
        if (KindOf(action.Deck) is { } kind)
        {
            run.Emit(new ManualEffectCreated(run.Context.Ids.NewId(), run.ActionTarget(action), kind, ManualEffectSource.Item, RunId: null));
        }
    }
}

/// <summary><c>spinWheel</c>: a loot wheel of the content picks an object by weight (only live ones), and it is given.</summary>
internal sealed class SpinWheelHandler : ActionHandler<SpinWheelAction>
{
    protected override void Execute(SpinWheelAction action, EffectRun run)
    {
        var catalog = run.State.Catalog;
        if (!catalog.Wheels.TryGetValue(action.Wheel, out var wheel))
        {
            return;
        }

        var entries = wheel.Entries.Where(e => e.Weight > 0 && catalog.Live(e.ObjectId) is not null).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var ticket = run.Context.Random.NextInt(0, entries.Sum(e => e.Weight));
        var entry = entries.First(e => (ticket -= e.Weight) < 0);
        var target = run.ActionTarget(action);
        run.Emit(new WheelSpun(target, wheel.Id, entry.ObjectId));
        run.Emit(Inventories.Give(run.State, run.Context, target, catalog.Live(entry.ObjectId)!, null, ObjectSource.Effect, run.UserId));
    }
}
