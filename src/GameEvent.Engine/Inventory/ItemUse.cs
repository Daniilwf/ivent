using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Inventory;

/// <summary>Using items and the admin's corrections of inventories (D-401, D-407).</summary>
internal static class ItemUse
{
    public static Decision Decide(SeasonState state, UseItem command, EngineContext context)
    {
        if (Guard(state, command.PlayerId, context) is { } refused)
        {
            return refused;
        }

        var player = state.Players[command.PlayerId];
        if (player.Wallet.Find(command.InstanceId) is not { } item)
        {
            return Decision.Reject(RejectionCodes.ItemUnknown, $"Object {command.InstanceId} is not in the player's inventory.");
        }

        var definition = state.Catalog.Get(item.ObjectId);
        if (definition.Kind != ObjectKind.Item)
        {
            return Decision.Reject(RejectionCodes.ItemNotAnItem, $"«{definition.Id}» is a {definition.Kind}: it acts on its own.");
        }

        if (!InWindow(state, player, definition.Window ?? UseWindow.Anytime))
        {
            return Decision.Reject(RejectionCodes.ItemWrongWindow, $"«{definition.Id}» is used {definition.Window}; the player is {player.Phase}.");
        }

        var used = new ObjectRemoved(player.PlayerId, item.InstanceId, item.ObjectId, ObjectRemoval.Used);

        // A text without automation goes to manual resolution (SPEC «Текстовые эффекты без автоматики», D-410).
        if (definition.Effect is not { } effect)
        {
            return Decision.Accept(
                used,
                new ItemUsed(player.PlayerId, item.InstanceId, item.ObjectId, [], []),
                new ManualEffectCreated(context.Ids.NewId(), player.PlayerId, DrawEvent: null, Effects.ManualEffectSource.Item, RunId: null) { ObjectId = definition.Id });
        }

        var (targets, noTarget) = Targets.ForUse(state, context, definition, player.PlayerId, command.TargetPlayerId);
        if (noTarget is not null)
        {
            return noTarget;
        }

        if (Problem(state, definition, player, targets) is { } problem)
        {
            return problem;
        }

        var choices = command.Choices.ToList();
        var afterUse = SeasonEngine.Apply(state, used);
        var itemUsed = new ItemUsed(player.PlayerId, item.InstanceId, item.ObjectId, [.. targets], [.. choices]);
        var lastRun = definition.Window == UseWindow.AfterDice ? LastCompleted(state, player)?.RunId : null;
        var run = new EffectRun(
            SeasonEngine.Apply(afterUse, itemUsed), context, player.PlayerId, definition.Id, definition.Hostile, item.Params, choices,
            lastRun is null ? DiceScope.Pending : DiceScope.AfterThrow, lastRun);
        run.Run(effect, targets);
        if (run.Refusal is { } refusal)
        {
            return refusal;
        }

        return run.UnusedChoices > 0
            ? Decision.Reject(RejectionCodes.ItemInvalidChoice, $"«{definition.Id}» asks for fewer answers than given.")
            : Decision.Accept([used, itemUsed, .. run.Events]);
    }

    public static Decision Decide(SeasonState state, AdjustInventory command, EngineContext context)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (!state.Rules.Features.Items)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Items are off in this season.");
        }

        if (state.Status is not (SeasonStatus.Draft or SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: inventories are frozen.");
        }

        if (!state.Players.TryGetValue(command.PlayerId, out var player))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Every admin change explains itself in the public log.");
        }

        if (command.Comment.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        var events = new List<IGameEvent> { new InventoryAdjusted(player.PlayerId, command.Comment.Trim()) };
        if (command.RemoveInstanceId is { } remove)
        {
            if (player.Wallet.Find(remove) is not { } held)
            {
                return Decision.Reject(RejectionCodes.ItemUnknown, $"Object {remove} is not in the player's inventory.");
            }

            events.Add(new ObjectRemoved(player.PlayerId, remove, held.ObjectId, ObjectRemoval.Admin));
        }

        if (command.GiveObjectId is { } give)
        {
            if (state.Catalog.Live(give) is not { } definition)
            {
                return Decision.Reject(RejectionCodes.ObjectUnknown, $"«{give}» is not in the season's content.");
            }

            var after = events.Aggregate(state, SeasonEngine.Apply);
            var given = Inventories.Give(after, context, player.PlayerId, definition, null, ObjectSource.Admin, fromPlayerId: null);
            if (given is ObjectLost)
            {
                return Decision.Reject(RejectionCodes.InventoryFull, $"The inventory is full ({state.Rules.Economy.InventoryLimit} items).");
            }

            events.Add(given);
        }

        return events.Count == 1
            ? Decision.Reject(RejectionCodes.NothingToChange, "Give an object or remove one.")
            : Decision.Accept(events);
    }

    /// <summary>
    /// A player's own economy action (items, shop, bets): the mechanic is on, the season runs and its deadline has not
    /// passed, the player is known and not the first finisher, whose inventory is frozen (SPEC).
    /// </summary>
    public static Decision? Guard(SeasonState state, Guid playerId, EngineContext context, Func<Rulesets.Features, bool>? feature = null)
    {
        if (SeasonSetup.RequireActive(state) is { } inactive)
        {
            return inactive;
        }

        if (!(feature ?? (f => f.Items))(state.Rules.Features))
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "This mechanic is off in this season.");
        }

        if (SeasonSetup.IsPastDeadline(state, context.Clock.UtcNow))
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed: no more turns this season.");
        }

        if (!state.Players.ContainsKey(playerId))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {playerId} is not in the season.");
        }

        return Targets.IsFirst(state, playerId)
            ? Decision.Reject(RejectionCodes.InventoryFrozen, "The first finisher's inventory is frozen.")
            : null;
    }

    /// <summary>
    /// The window of use (SPEC «Окна использования»): before a roll — waiting for it; after a roll — a game offered;
    /// before the throw — playing; after the throw — right after a completion, before the next roll; any time — any, but not
    /// while a move waits at a fork.
    /// </summary>
    public static bool InWindow(SeasonState state, SeasonPlayer player, UseWindow window)
    {
        if (player.Choice?.Kind == ChoiceKind.Branch)
        {
            return false;
        }

        return window switch
        {
            UseWindow.BeforeRoll => player.Phase == TurnPhase.Idle,
            UseWindow.AfterRoll => player.Phase == TurnPhase.Rolling,
            UseWindow.BeforeDice => player.Phase == TurnPhase.Playing,
            UseWindow.AfterDice => player.Phase == TurnPhase.Idle && LastCompleted(state, player) is not null,
            _ => true,
        };
    }

    /// <summary>The player's latest run, when it is a completed one: the throw «after the throw» works on.</summary>
    public static RunState? LastCompleted(SeasonState state, SeasonPlayer player) =>
        state.Runs.Values.Where(r => r.PlayerId == player.PlayerId).MaxBy(r => r.StartedAt) is { Status: RunStatus.Completed } run
        && !run.FreeMode
            ? run
            : null;

    // What stops a use that has targets: a move of a player at a fork, «не стакается с собой», the limit of hostile effects.
    private static Decision? Problem(SeasonState state, ObjectDefinition definition, SeasonPlayer user, IReadOnlyList<Guid> targets)
    {
        var effect = definition.Effect!;
        var actions = effect.Actions.Concat(effect.Outcomes?.Cases.SelectMany(c => c.Actions) ?? []).ToList();
        if (actions.Any(a => a is MoveAction or TeleportAction)
            && targets.Append(user.PlayerId).Any(t => state.Players[t].Choice?.Kind == ChoiceKind.Branch))
        {
            return Decision.Reject(RejectionCodes.BranchChoicePending, "A player it would move is choosing a branch: that step has begun.");
        }

        if (!definition.Stackable && Stacks(state, definition, user, targets, actions))
        {
            return Decision.Reject(RejectionCodes.ItemNotStackable, $"«{definition.Id}» does not stack with itself.");
        }

        var cap = state.Rules.Effects.HostileCap;
        if (definition.Hostile && cap.Enabled
            && targets.Where(t => t != user.PlayerId).Any(t => state.Players[t].Wallet.Inventory.Count(o => o.Hostile) >= cap.MaxActive))
        {
            return Decision.Reject(RejectionCodes.ItemHostileCap, $"A target already carries {cap.MaxActive} hostile effect(s).");
        }

        return null;
    }

    // Another use of the same item still waiting (a change of the next roll or throw), already in the throw it would
    // change, or the object it gives still held by the target.
    private static bool Stacks(SeasonState state, ObjectDefinition definition, SeasonPlayer user, IReadOnlyList<Guid> targets, List<ActionSpec> actions)
    {
        var id = definition.Id;
        foreach (var target in targets.Append(user.PlayerId).Distinct())
        {
            var wallet = state.Players[target].Wallet;
            if (wallet.NextRoll.Any(m => m.ObjectId == id) || wallet.NextDice.Any(m => m.ObjectId == id))
            {
                return true;
            }
        }

        if (definition.Window == UseWindow.AfterDice && LastCompleted(state, user) is { } run && run.DiceMods.Sources.Contains(id))
        {
            return true;
        }

        var given = actions.OfType<GiveObjectAction>().Select(a => a.ObjectId).ToHashSet(StringComparer.Ordinal);
        return targets.Any(t => state.Players[t].Wallet.Inventory.Any(o => given.Contains(o.ObjectId)));
    }
}
