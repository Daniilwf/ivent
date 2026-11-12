using System.Collections.Immutable;
using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Effects.Actions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Inventory;

/// <summary>What the player answers when using an item, in the order the engine asks (D-407).</summary>
public enum ItemStepKind
{
    /// <summary>A <c>requestChoice</c>: one of <see cref="ItemStep.Options"/>.</summary>
    Choice,

    /// <summary>
    /// A <c>takeObject</c> with <c>pick: chosen</c>: objects of the target (<see cref="ItemStep.ObjectsByTarget"/>, instance
    /// ids) — <see cref="ItemStep.Count"/> different ones, or with <see cref="ItemStep.Ranked"/> any number in order of preference.
    /// </summary>
    PickObject,

    /// <summary>A <c>move</c> with <c>direction: chosen</c>: <see cref="MoveHandler.Forward"/> or <see cref="MoveHandler.Back"/> (D-477).</summary>
    Direction,
}

/// <summary>
/// One answer of a use: its kind, the prompt and source of a choice and the answers the engine accepts — for an object,
/// by target, since whose objects they are depends on the target picked first. A pick of objects asks
/// <see cref="Count"/> different ones (as many as the owner has, when fewer), or with <see cref="Ranked"/> a count known only
/// at the use (<c>$roll</c>): any number of different ones as a list of preference, the rest taken at random (D-476).
/// </summary>
public sealed record ItemStep(
    ItemStepKind Kind,
    string? Prompt,
    ChoiceSource? From,
    EquatableArray<string> Options,
    ImmutableSortedDictionary<Guid, EquatableArray<string>>? ObjectsByTarget = null,
    int Count = 1,
    bool Ranked = false);

/// <summary>
/// What the screen needs to offer an object of the inventory (D-440): its definition, whether «Использовать» is open now
/// (<see cref="Refusal"/> — the engine's code when it is not), whether the player picks the target and the players the
/// engine accepts as that target, and the answers to give. Built from the same rules <see cref="UseItem"/> checks, so the
/// screen never offers what the engine refuses; what depends on the target or on chance (a full inventory, «не
/// стакается», the hostile cap) the engine still says on use.
/// </summary>
public sealed record ItemPlan(
    InventoryObject Item,
    ObjectDefinition Definition,
    string? Refusal,
    bool ChoosesTarget,
    EquatableArray<Guid> Targets,
    EquatableArray<ItemStep> Steps)
{
    public bool Usable => Refusal is null;
}

/// <summary>The plans of a player's inventory (D-440): read-only, from the folded state, the pool and the server's clock.</summary>
public static class ItemPlans
{
    /// <summary>Every object <paramref name="playerId"/> holds, in the order they came; empty for a player who is not in the season.</summary>
    public static IReadOnlyList<ItemPlan> Of(SeasonState state, IPoolView pool, DateTimeOffset now, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pool);
        if (!state.Players.TryGetValue(playerId, out var player))
        {
            return [];
        }

        var guard = ItemUse.Guard(state, playerId, now)?.Rejection?.Code;
        return [.. player.Wallet.Inventory.OrderBy(o => o.Acquired).Select(item => Plan(state, pool, player, item, guard))];
    }

    private static ItemPlan Plan(SeasonState state, IPoolView pool, SeasonPlayer player, InventoryObject item, string? guard)
    {
        var definition = state.Catalog.Get(item.ObjectId);
        var refusal = definition.Kind != ObjectKind.Item
            ? RejectionCodes.ItemNotAnItem
            : guard
                ?? (!ItemUse.InWindow(state, player, definition.Window ?? UseWindow.Anytime) ? RejectionCodes.ItemWrongWindow
                    : definition.UseCondition is { } condition && !Conditions.Hold(state, pool, player.PlayerId, condition, runId: null) ? RejectionCodes.ItemConditionNotMet
                    : null);
        if (definition.Kind != ObjectKind.Item || definition.Effect is not { } effect)
        {
            return new ItemPlan(item, definition, refusal, false, [], []);
        }

        var chooses = effect.Target?.Selector == TargetSelector.Chosen;
        var targets = chooses ? Targets.Choosable(state, definition, player.PlayerId) : [];
        if (refusal is null && (chooses ? targets.Count == 0 : Targets.HasNobody(state, definition, player.PlayerId)))
        {
            refusal = RejectionCodes.ItemNoTarget;
        }

        // The item being used is spent before its effect runs, so it is never an answer nor a thing to give
        IReadOnlyList<string> Objects(Guid owner, TakeObjectAction take) =>
            [.. ObjectFilters.Matching(state, owner, take.Filter, moves: take.Mode != TakeMode.Destroy)
                .Where(o => o.InstanceId != item.InstanceId)
                .Select(o => o.InstanceId.ToString("N"))];

        var steps = new List<ItemStep>();

        // A branch by the player's answer asks it first; its cases ask nothing more (ContentPublishing), and a branch by a
        // roll asks nothing at all. The effect's own actions run only without a branch.
        if (effect.Outcomes is { Choice: { } branch })
        {
            steps.Add(new ItemStep(ItemStepKind.Choice, branch.Prompt, branch.Options.From, [.. RequestChoiceHandler.Options(branch.Options, state, pool)]));
        }

        foreach (var action in effect.Outcomes is null ? effect.Actions : [])
        {
            switch (action)
            {
                case RequestChoiceAction request:
                    steps.Add(new ItemStep(ItemStepKind.Choice, request.Prompt, request.Options.From, [.. RequestChoiceHandler.Options(request.Options, state, pool)]));
                    break;
                case MoveAction { Direction: MoveDirection.Chosen }:
                    steps.Add(new ItemStep(ItemStepKind.Direction, null, null, [MoveHandler.Forward, MoveHandler.Back]));
                    break;
                case TakeObjectAction { Mode: TakeMode.Give } give when refusal is null && Objects(player.PlayerId, give).Count == 0:
                    // Nothing it may give is known now; the engine refuses the use (D-478)
                    refusal = RejectionCodes.ItemNothingToGive;
                    break;
                case TakeObjectAction { Pick: Pick.Chosen, All: false } take:
                    // Whose objects: the user's own (a «give» or target «self»), else each player the target may be (a
                    // selector that picks the target itself never asks, ContentPublishing). An owner with none asks no answer.
                    var owners = take.Mode == TakeMode.Give || take.Target == ActionTarget.Self || !chooses ? [player.PlayerId] : targets;
                    var ranked = take.Count is { Kind: ContentValueKind.Reference };
                    steps.Add(new ItemStep(
                        ItemStepKind.PickObject,
                        null,
                        null,
                        [],
                        owners.ToImmutableSortedDictionary(id => id, id => (EquatableArray<string>)[.. Objects(id, take)]),
                        ranked ? 1 : take.Count?.Number ?? 1,
                        ranked));
                    break;
                default:
                    break;
            }
        }

        return new ItemPlan(item, definition, refusal, chooses, [.. targets], [.. steps]);
    }
}
