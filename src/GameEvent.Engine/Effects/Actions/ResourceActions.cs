using GameEvent.Engine.Content;
using GameEvent.Engine.Scoring;

namespace GameEvent.Engine.Effects.Actions;

/// <summary>
/// <c>changeResource</c>: points and coins are their own measures (SPEC «Каждый эффект явно указывает, на что
/// действует»), any other key is a resource of the dictionary. Coins go below zero only when
/// <c>economy.allowNegativeCoins</c> allows it; otherwise an effect takes at most what the player has.
/// </summary>
internal sealed class ChangeResourceHandler : ActionHandler<ChangeResourceAction>
{
    public const string Points = "points";
    public const string Coins = "coins";

    protected override void Execute(ChangeResourceAction action, EffectRun run)
    {
        var amount = run.Number(action.Amount);
        var target = run.ActionTarget(action);
        switch (action.Resource)
        {
            case Points when amount != 0:
                run.Emit(new PointsChanged(target, amount, PointsReason.Item, RunId: null));
                break;
            case Coins:
                var coins = run.State.Players[target].Coins;
                if (!run.State.Rules.Economy.AllowNegativeCoins && coins + amount < 0)
                {
                    amount = -Math.Max(0, coins);
                }

                if (amount != 0)
                {
                    run.Emit(new CoinsChanged(target, amount, CoinsReason.Item, RunId: null));
                }

                break;
            case Points:
                break;
            default:
                if (amount != 0)
                {
                    run.Emit(new ResourceChanged(target, action.Resource, amount, ResourceReason.Item));
                }

                break;
        }
    }
}

/// <summary><c>roll</c>: dice rolled now and logged; the total is <c>$roll</c> for the actions after it.</summary>
internal sealed class RollHandler : ActionHandler<RollAction>
{
    protected override void Execute(RollAction action, EffectRun run) => run.LastRoll = run.Number(action.Dice);
}
