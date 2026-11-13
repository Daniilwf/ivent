using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Runs;

namespace GameEvent.Engine.Effects.Actions;

/// <summary>
/// <c>modifyNextRoll</c>: the target's next roll gets a filter (its <c>$parameters</c> resolved) or a choice of several
/// games, or a condition on the run (D-507). It waits for the next roll from Idle and holds for its rerolls (D-405).
/// </summary>
internal sealed class ModifyNextRollHandler : ActionHandler<ModifyNextRollAction>
{
    protected override void Execute(ModifyNextRollAction action, EffectRun run)
    {
        var target = run.ActionTarget(action);
        var filter = action.Filter is { } f
            ? f with { Tags = f.Tags is { } tags ? [.. tags.Select(run.Text)] : null }
            : null;
        var hostile = run.Hostile && target != run.UserId;
        if (filter is not null || action.ChoiceCount is not null)
        {
            run.Emit(new NextRollModified(target, new RollModifier(run.ObjectId, filter, action.ChoiceCount, hostile, run.EventKind)));
        }

        // The condition is a change of its own: a contradicting one waits for a later roll without the filter (D-506, D-591)
        if (action.RunCondition is { } condition)
        {
            run.Emit(new NextRollModified(target, new RollModifier(run.ObjectId, null, null, hostile, run.EventKind) { RunCondition = condition }));
        }
    }
}

/// <summary>
/// <c>modifyDice</c>: a stage of the throw's pipeline (D-14, D-408). <c>current</c> changes the throw being made
/// (<c>beforeDice</c>) or just made (<c>afterDice</c>); otherwise the change waits for the target's next throw.
/// </summary>
internal sealed class ModifyDiceHandler : ActionHandler<ModifyDiceAction>
{
    protected override void Execute(ModifyDiceAction action, EffectRun run)
    {
        var target = run.ActionTarget(action);
        var value = run.Resolved(action.Value);
        var ownThrow = run.RunId is { } runId && run.State.Runs[runId].PlayerId == target;
        if (action.When == DiceWhen.Current && ownThrow && run.Dice == DiceScope.Throw)
        {
            run.ThrowChanges.Add(new ThrowChange(run.ObjectId, action.Stage, value, action.Sides, action.From, action.Filter));
            return;
        }

        if (action.When == DiceWhen.Current && ownThrow && run.Dice == DiceScope.AfterThrow)
        {
            DicePipeline.AfterThrow(run, run.RunId!.Value, new ThrowChange(run.ObjectId, action.Stage, value, action.Sides, action.From, action.Filter));
            return;
        }

        run.Emit(new NextDiceModified(target, new DiceModifier(run.ObjectId, action.Stage, value, run.Hostile && target != run.UserId, action.Sides, action.From, run.EventKind)));
    }
}

/// <summary>
/// <c>requestChoice</c>: the player's answer, given with the use (D-407), checked against the options the server computes:
/// the categories of the pool, the season's players, the games of the pool or a fixed list. The answer is <c>$choice</c>.
/// </summary>
internal sealed class RequestChoiceHandler : ActionHandler<RequestChoiceAction>
{
    protected override void Execute(RequestChoiceAction action, EffectRun run) => run.NextChoice(Options(action.Options, run));

    public static IReadOnlyCollection<string> Options(ChoiceOptionsSpec options, EffectRun run) => Options(options, run.State, run.Context.Pool);

    /// <summary>The answers the engine accepts, in order: the screen offers the same (D-440).</summary>
    public static IReadOnlyList<string> Options(ChoiceOptionsSpec options, Seasons.SeasonState state, Pool.IPoolView pool) =>
        options.From switch
        {
            ChoiceSource.Categories => [.. pool.Categories.Where(c => c.Weight > 0).Select(c => c.Name)],
            ChoiceSource.Players => [.. state.Players.Keys.Select(id => id.ToString("N"))],
            ChoiceSource.Games => [.. pool.Games.Where(g => !g.IsDeleted).Select(g => g.Id.ToString("N"))],
            _ => [.. options.List ?? []],
        };
}
