using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Turns;

internal static class Choosing
{
    public static Decision Decide(SeasonState state, MakeChoice command, EngineContext context)
    {
        if (state.IsCreated && state.Players.TryGetValue(command.PlayerId, out var chooser) && chooser.Choice is { Kind: ChoiceKind.Branch } branch)
        {
            return DecideBranch(state, chooser, branch, command, context);
        }

        if (TurnRules.Check(state, command.PlayerId, command, context.Clock.UtcNow) is { } rejection)
        {
            return rejection;
        }

        var choice = state.Players[command.PlayerId].Choice!;
        if (choice.ChoiceId != command.ChoiceId)
        {
            // An answer from a stale tab to a choice that is gone.
            return Decision.Reject(RejectionCodes.NoPendingChoice, $"Choice {command.ChoiceId} is not pending.");
        }

        if (choice.Options.SingleOrDefault(o => o.Id == command.OptionId) is not { } option)
        {
            return Decision.Reject(RejectionCodes.UnknownChoiceOption, $"The option is not one of choice {command.ChoiceId}.");
        }

        // SPEC «Игровой цикл»: Choosing --> Playing. The picked game starts at once, with the rules of the roll (D-91).
        var game = option.Game ?? throw new InvalidOperationException($"Game choice {choice.ChoiceId} has an option without a game.");
        return Decision.Accept(
            new ChoiceMade(command.PlayerId, command.ChoiceId, command.OptionId),
            new RunStarted(context.Ids.NewId(), command.PlayerId, game.GameId, game.Snapshot, game.RolledAt, context.Clock.UtcNow));
    }

    /// <summary>
    /// The branch at a fork (D-304): the paused move goes on into the picked cell, and may reach the finish, stop on a
    /// cell or pause at the next fork. Allowed after the deadline and while closing: the steps belong to a throw made
    /// before it (D-305).
    /// </summary>
    private static Decision DecideBranch(SeasonState state, SeasonPlayer player, PendingChoice choice, MakeChoice command, EngineContext context)
    {
        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonNotActive, $"The season is {state.Status}.");
        }

        if (choice.ChoiceId != command.ChoiceId)
        {
            return Decision.Reject(RejectionCodes.NoPendingChoice, $"Choice {command.ChoiceId} is not pending.");
        }

        if (choice.Options.All(o => o.Id != command.OptionId))
        {
            return Decision.Reject(RejectionCodes.UnknownChoiceOption, $"The option is not one of choice {command.ChoiceId}.");
        }

        var move = choice.Move ?? throw new InvalidOperationException($"Branch choice {choice.ChoiceId} has no paused move.");
        var run = state.Runs[move.RunId ?? throw new InvalidOperationException($"Branch choice {choice.ChoiceId} has no run.")];
        var made = new ChoiceMade(player.PlayerId, choice.ChoiceId, command.OptionId);
        var after = Apply(state, made);
        var walk = Map.Movement.WalkOwn(state.Map, player.CellId, move.Steps, command.OptionId);
        var moved = new Map.PlayerMoved(
            player.PlayerId, player.CellId, walk.Path[^1], move.Steps, [.. walk.Path], move.Reason, move.RunId, walk.Paused);
        after = SeasonEngine.Apply(after, moved);
        return Decision.Accept([made, moved, .. RunLifecycle.AfterOwnMove(after, after.Players[player.PlayerId], run, moved, walk, context)]);
    }

    public static SeasonState Apply(SeasonState state, Map.BranchChoiceRequested e)
    {
        var choice = new PendingChoice(e.ChoiceId, ChoiceKind.Branch, [.. e.Options.Select(cell => new ChoiceOption(cell, Game: null))])
        {
            Move = new PendingMove(e.Steps, e.Reason, e.RunId),
        };
        return Update(state, e.PlayerId, p => p with { Choice = choice });
    }

    public static SeasonState Apply(SeasonState state, GameChoiceRolled e)
    {
        var choice = new PendingChoice(
            e.ChoiceId, ChoiceKind.Game, [.. e.Offers.Select(o => new ChoiceOption(o.GameId.ToString("N"), o))]);
        return Update(state, e.PlayerId, p => p with { Phase = TurnPhase.Rolling, Offer = null, Choice = choice });
    }

    public static SeasonState Apply(SeasonState state, ChoiceMade e) =>
        Update(state, e.PlayerId, p =>
        {
            var option = p.Choice!.Options.Single(o => o.Id == e.OptionId);
            return p.Choice.Kind switch
            {
                ChoiceKind.Game => p with { Offer = option.Game, Choice = null },
                ChoiceKind.Branch => p with { Choice = null },
                _ => throw new InvalidOperationException($"Unknown choice kind {p.Choice.Kind}."),
            };
        });

    public static SeasonState Apply(SeasonState state, ChoiceDiscarded e) =>
        Update(state, e.PlayerId, p => p with { Phase = TurnPhase.Idle, Choice = null, RerollsThisRoll = 0 });

    private static SeasonState Update(SeasonState state, Guid playerId, Func<SeasonPlayer, SeasonPlayer> change) =>
        state with { Players = state.Players.SetItem(playerId, change(state.Players[playerId])) };
}
