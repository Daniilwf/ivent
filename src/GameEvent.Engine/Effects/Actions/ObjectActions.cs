using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Effects.Actions;

/// <summary>Which objects of an inventory an action may take or change (CONTENT.md <c>filter</c>), in the order they came.</summary>
internal static class ObjectFilters
{
    /// <summary>
    /// The player's objects the filter lets through, without those the action may not touch (D-478): an unstealable one
    /// never changes hands (<paramref name="moves"/>), an undestroyable one is never destroyed or transformed.
    /// </summary>
    public static IReadOnlyList<InventoryObject> Matching(EffectRun run, Guid playerId, ObjectFilterSpec? filter, bool moves = false) =>
        Matching(run.State, playerId, filter, moves);

    /// <inheritdoc cref="Matching(EffectRun, Guid, ObjectFilterSpec?, bool)"/>
    public static IReadOnlyList<InventoryObject> Matching(Seasons.SeasonState state, Guid playerId, ObjectFilterSpec? filter, bool moves = false) =>
        [.. state.Players[playerId].Wallet.Inventory
            .Where(o => state.Catalog.Get(o.ObjectId) is var definition
                && (filter is null || Matches(definition, o, filter))
                && (moves ? !definition.Unstealable : !definition.Undestroyable))
            .OrderBy(o => o.Acquired)];

    private static bool Matches(ObjectDefinition definition, InventoryObject item, ObjectFilterSpec filter) =>
        (filter.Kind is not { } kind || item.Kind == kind)
        && (filter.Rarity is not { } rarity || definition.Rarity == rarity)
        && (filter.ObjectId is not { } id || item.ObjectId == id)
        && (filter.Hostile is not { } hostile || definition.Hostile == hostile)
        && (filter.HasCounter is not { } counted || (item.Counter is not null) == counted)
        && (filter.ExcludeTags is not { } excluded || !(definition.Tags ?? []).Any(t => excluded.Contains(t, StringComparer.Ordinal)));

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
/// fit is lost, D-406), <c>destroy</c> removes it. Several objects with <c>count</c> or <c>all</c> (D-476): a fixed count
/// chosen asks one answer per object; a count known only at the use (<c>$roll</c>) takes the answers left as a list of
/// preference and the rest at random.
/// </summary>
internal sealed class TakeObjectHandler : ActionHandler<TakeObjectAction>
{
    protected override void Execute(TakeObjectAction action, EffectRun run)
    {
        // Giving hands the user's own object to the target; the other modes work on the target's objects
        var giving = action.Mode == TakeMode.Give;
        var from = giving ? run.UserId : run.ActionTarget(action);
        var to = giving ? run.ActionTarget(action) : run.UserId;
        if (giving && ObjectFilters.Matching(run, from, action.Filter, moves: true).Count == 0)
        {
            run.Refuse(RejectionCodes.ItemNothingToGive, $"The user of «{run.ObjectId}» has nothing it may give.");
            return;
        }

        if (!giving && action.Required && ObjectFilters.Matching(run, from, action.Filter, moves: action.Mode != TakeMode.Destroy).Count == 0)
        {
            run.Refuse(RejectionCodes.ItemNothingToTake, $"The target of «{run.ObjectId}» has nothing it may take.");
            return;
        }

        foreach (var item in Picked(action, run, from))
        {
            Take(action, run, from, to, item);
            if (run.Refusal is not null)
            {
                return;
            }
        }
    }

    private static List<InventoryObject> Picked(TakeObjectAction action, EffectRun run, Guid from)
    {
        var candidates = ObjectFilters.Matching(run, from, action.Filter, moves: action.Mode != TakeMode.Destroy).ToList();
        if (action.All)
        {
            return candidates;
        }

        var count = action.Count is { } value ? run.Number(value) : 1;
        var picked = new List<InventoryObject>();
        if (action.Pick == Pick.Chosen && action.Count is { Kind: ContentValueKind.Reference })
        {
            if (run.RemainingChoices([.. candidates.Select(o => o.InstanceId.ToString("N"))]) is not { } preferred)
            {
                return [];
            }

            foreach (var answer in preferred.Take(count))
            {
                var chosen = candidates.Single(o => o.InstanceId.ToString("N") == answer);
                picked.Add(chosen);
                candidates.Remove(chosen);
            }
        }

        // A fixed count chosen asks one answer per object; the rest of a count known only now goes at random
        var pick = action.Pick == Pick.Chosen && action.Count is not { Kind: ContentValueKind.Reference } ? Pick.Chosen : Pick.Random;
        while (picked.Count < count && candidates.Count > 0)
        {
            if (ObjectFilters.Pick(run, candidates, pick) is not { } next)
            {
                return [];
            }

            picked.Add(next);
            candidates.Remove(next);
        }

        return picked;
    }

    private static void Take(TakeObjectAction action, EffectRun run, Guid from, Guid to, InventoryObject item)
    {
        if (action.Mode == TakeMode.Destroy)
        {
            run.Emit(new ObjectRemoved(from, item.InstanceId, item.ObjectId, ObjectRemoval.Destroyed));
            return;
        }

        if (to == from)
        {
            return;
        }

        if (item.Kind == ObjectKind.Item && run.State.Players[to].Wallet.Items >= run.State.Rules.Economy.InventoryLimit)
        {
            // An undestroyable object that has no room is not lost: it stays where it is (D-478, AU4-2)
            if (run.State.Catalog.Get(item.ObjectId).Undestroyable)
            {
                return;
            }

            run.Emit(new ObjectRemoved(from, item.InstanceId, item.ObjectId, ObjectRemoval.Destroyed));
            run.Emit(new ObjectLost(to, item.ObjectId));
            return;
        }

        run.Emit(new ObjectTransferred(from, to, item.InstanceId, item.ObjectId, Given: action.Mode == TakeMode.Give));
    }
}

/// <summary><c>transformObject</c>: one matching object at random turns into another (its lifetime starts anew) or gets a note.</summary>
internal sealed class TransformObjectHandler : ActionHandler<TransformObjectAction>
{
    protected override void Execute(TransformObjectAction action, EffectRun run)
    {
        var owner = run.ActionTarget(action);
        if (action.Mode is TransformMode.AddCounter or TransformMode.ResetCounters)
        {
            Counters(action, run, owner);
            return;
        }

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

    // Every matching countdown changes; one brought to zero goes off at once, for its owner (D-479). Only a countdown that
    // crosses zero goes off: one at zero already is left alone, so an effect that lowers its own counter goes off once
    // (AU4-1). Each is read again before it changes: one going off before it may have changed or taken it.
    private static void Counters(TransformObjectAction action, EffectRun run, Guid owner)
    {
        var by = action.Value is { } value ? run.Number(value) : 0;
        foreach (var matched in ObjectFilters.Matching(run, owner, action.Filter).Where(o => o.Counter is > 0))
        {
            if (run.State.Players[owner].Wallet.Find(matched.InstanceId) is not { Counter: > 0 } item)
            {
                continue;
            }

            var start = run.State.Catalog.Get(item.ObjectId).Effect?.Counter?.Start ?? item.Counter!.Value;
            var counter = action.Mode == TransformMode.ResetCounters ? start : item.Counter!.Value + by;
            if (counter > 0)
            {
                if (counter != item.Counter)
                {
                    run.Emit(new ObjectChanged(owner, item with { Counter = counter }));
                }

                continue;
            }

            var spent = item with { Counter = 0 };
            run.Emit(new ObjectChanged(owner, spent));
            Effects.Firing.GoOff(run, owner, spent);
            if (run.Cut is not null)
            {
                return;
            }
        }
    }
}

/// <summary>
/// <c>drawEvent</c>: the target draws an event from the deck and plays it at once (D-500, D-501); while
/// <c>features.events</c> is off the event is drawn by hand — a manual effect «draw a good/bad event» (D-10, D-410).
/// </summary>
internal sealed class DrawEventHandler : ActionHandler<DrawEventAction>
{
    protected override void Execute(DrawEventAction action, EffectRun run) =>
        run.Emit(Decks.EventDraws.Draw(run.State, run.Context, run.ActionTarget(action), action.Deck, Decks.EventSource.Item));
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

        var entry = Weighted.Pick(entries, run.Context.Random);
        var target = run.ActionTarget(action);
        run.Emit(new WheelSpun(target, wheel.Id, entry.ObjectId));
        run.Emit(Inventories.Give(run.State, run.Context, target, catalog.Live(entry.ObjectId)!, null, ObjectSource.Effect, run.UserId));
    }
}
